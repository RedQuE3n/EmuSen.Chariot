namespace EmuSen.Chariot

open System
open EmuSen.Pegasus

/// One piece of undelivered post.
type Post =
    { Id: int64
      Sender: Handle
      Channel: Channel
      Payload: byte[] }

/// What happened when post was offered.
type Accepted =
    /// Stored, and this is the row a recipient acknowledges.
    | Queued of id: int64
    /// The recipient's message queue is at its cap and nothing was stored. The
    /// sender is told; see Server.Route.
    | Full of held: int

/// Sealed payloads held for somebody, and handed over when they are there.
///
/// **THIS MODULE USED TO BE TRIVIAL AND IS NOT ANY MORE, AND THE REASON IS THE
/// MOST IMPORTANT THING ON THIS PAGE.** Its original comment said that every
/// hard part of a message queue — ordering, exactly-once, acknowledgement,
/// deduplication — was absent, and that this was not cleverness but a gift:
/// `Pegasus_Sync.md` §4 establishes that payloads are Yjs updates, so delivery
/// is idempotent and order-independent, and `docs/Chariot_Design.md` §6.1
/// argued a bounded queue is safe because **every peer keeps a complete replica
/// on its own disk**, so a dropped blob costs promptness and never content.
///
/// Every clause of that is true of a note edit and false of an instant message:
///
/// - **Not idempotent.** Hand the same update over twice and the document is
///   unchanged. Hand the same MESSAGE over twice and it is in the transcript
///   twice.
/// - **Not order-independent.** Updates merge to the same document in any
///   order. A conversation read out of order is a different conversation.
/// - **NOT RECOVERABLE IF DROPPED, which is the one that matters.** There is no
///   second replica to converge with. A message this server discards is gone
///   from the world, and §6.1's "what is lost is promptness, not content"
///   becomes exactly false. The 512-item trim would have silently destroyed
///   people's post.
///
/// So the two channels are stored in one table and treated differently, and
/// that is the whole shape of this module. Notes keep the old behaviour, which
/// was always correct for them. Messages are stored until ACKNOWLEDGED, and a
/// full queue REFUSES new post and tells the sender rather than quietly evicting
/// the oldest. The correction is written up as `docs/Chariot_Design.md` §13.
module Mailbox =

    /// Kept per recipient rather than globally, so one busy correspondent
    /// cannot evict everybody else's post — and, on the message channel, so one
    /// correspondent cannot fill the disk and have everybody else's senders
    /// refused.
    [<Literal>]
    let DefaultLimit = 512

    /// Note post older than this is not worth delivering: a peer that has been
    /// away a fortnight resynchronises from a state vector on reconnect anyway,
    /// which is cheaper than replaying a fortnight of deltas.
    ///
    /// **This applies to the note channel and to nothing else.** Ageing a
    /// message out is the same silent loss the trim was, wearing a clock: a
    /// person who was away three weeks wants the messages, and "they would have
    /// resynchronised anyway" has no meaning for a conversation. A message is
    /// deleted when it is acknowledged, and otherwise it waits.
    [<Literal>]
    let DefaultMaxAgeDays = 14.0

    /// The storage encoding of a channel, which is deliberately NOT the wire
    /// tag. They happen to agree today; a build that assumed they must would
    /// break the day one of them needs a value the other does not have, and the
    /// failure would be rows delivered on the wrong policy.
    let private ordinal channel =
        match channel with
        | NoteTraffic -> 0
        | MessageTraffic -> 1

    let private ofOrdinal value =
        if value = 1 then MessageTraffic else NoteTraffic

    /// Queues one payload.
    ///
    /// On the NOTE channel this is what it always was: insert, then trim the
    /// recipient's note queue back to the limit, oldest first. Oldest first
    /// because a Yjs update carries operations later updates do not repeat, so
    /// the newest are the ones a recipient is most likely to still need in
    /// isolation — and because the alternative, refusing new post once full,
    /// would make a full queue permanently full.
    ///
    /// On the MESSAGE channel the trim is exactly backwards and the refusal is
    /// exactly right, for the same reason reversed: the oldest message is not
    /// superseded by anything, so evicting it destroys it. The insert is
    /// conditional on the count in ONE statement rather than a count followed by
    /// an insert, so two senders arriving together cannot both pass a check and
    /// then both write. SQLite evaluates the sub-select as part of the insert;
    /// rows-affected is the answer.
    let put (dbPath: string) (limit: int) (channel: Channel) (recipient: Handle) (sender: Handle) (payload: byte[]) =
        use db = Db.openAt dbPath
        let now = DateTime.UtcNow.ToString "o"

        match channel with
        | NoteTraffic ->
            Db.executeWith
                db
                "INSERT INTO mailbox (recipient, sender, payload, queued_at, channel)
                 VALUES ($recipient, $sender, $payload, $now, $channel)"
                [ "$recipient", box recipient.Folded
                  "$sender", box sender.Folded
                  "$payload", box payload
                  "$now", box now
                  "$channel", box (ordinal channel) ]
            |> ignore

            // Trims the note queue only. A note-channel flood must not be able
            // to evict somebody's messages, which is what a shared cap would
            // let it do.
            Db.executeWith
                db
                "DELETE FROM mailbox WHERE id IN (
                     SELECT id FROM mailbox WHERE recipient = $recipient AND channel = 0
                     ORDER BY id DESC LIMIT -1 OFFSET $limit)"
                [ "$recipient", box recipient.Folded; "$limit", box limit ]
            |> ignore

            // ZERO, and it is the same zero the envelope uses to mean "there is
            // nothing here to acknowledge" (Types.fs). Nothing acknowledges a
            // note, so this row's real id is of no use to any caller: an earlier
            // draft read it back with `SELECT last_insert_rowid()` and returned
            // it, which cost an extra query per queued update to produce a
            // number every call site immediately discarded — and which would
            // have invited somebody to acknowledge a note. §13.3 records what
            // that extra query cost, because it was not free: it slowed the
            // note path enough to expose a latent race in this project's own
            // test for the queue bound.
            Queued 0L

        | MessageTraffic ->
            // THE BOUND IS THE `WHERE`, and it belongs inside this statement
            // rather than in a COUNT before it. Counting first and inserting
            // second is two statements with a gap in the middle: two senders
            // posting to the same absent recipient at once both read "one short
            // of the limit" and both insert, so the cap is exceeded by as many
            // senders as happen to be talking. INSERT ... SELECT ... WHERE is one
            // statement and SQLite settles it.
            //
            // Zero rows written is therefore not an error - it IS the refusal,
            // and the COUNT below turns it into a number the sender is told.
            //
            // This said `WHERE 1 = 1` and shipped that way. Always true, so the
            // insert always succeeded, `written` was always 1, `Full` was
            // unreachable, and $limit was bound as a parameter the SQL never
            // mentioned. The message queue had no bound and no sender was ever
            // told a message had not landed. The test for it could not fail: it
            // waited on an Undeliverable that never came, through a read with no
            // timeout, so the suite HUNG instead of going red - 10m20s in CI
            // reporting nothing. A guard whose test cannot fail is not a guard.
            let written =
                Db.executeWith
                    db
                    "INSERT INTO mailbox (recipient, sender, payload, queued_at, channel)
                     SELECT $recipient, $sender, $payload, $now, $channel
                     WHERE (SELECT COUNT(*) FROM mailbox
                            WHERE recipient = $recipient AND channel = 1) < $limit"
                    [ "$recipient", box recipient.Folded
                      "$sender", box sender.Folded
                      "$payload", box payload
                      "$now", box now
                      "$channel", box (ordinal channel)
                      "$limit", box limit ]

            if written = 1 then
                Db.query db "SELECT last_insert_rowid()" [] (fun r -> r.GetInt64 0)
                |> List.head
                |> Queued
            else
                Db.query
                    db
                    "SELECT COUNT(*) FROM mailbox WHERE recipient = $recipient AND channel = 1"
                    [ "$recipient", box recipient.Folded ]
                    (fun r -> r.GetInt32 0)
                |> List.head
                |> Full

    /// Everything held for a recipient, oldest first.
    let peek (dbPath: string) (recipient: Handle) =
        use db = Db.openAt dbPath

        Db.query
            db
            "SELECT id, sender, payload, channel FROM mailbox WHERE recipient = $recipient ORDER BY id"
            [ "$recipient", box recipient.Folded ]
            (fun r ->
                { Id = r.GetInt64 0
                  Sender = Handle.Parse(r.GetString 1)
                  Payload = r.GetFieldValue<byte[]> 2
                  Channel = ofOrdinal (r.GetInt32 3) })

    /// Removes post that has been handed over.
    ///
    /// Taken by id rather than "everything for this recipient", so post that
    /// arrived while a drain was running is not deleted undelivered. On the
    /// message channel this is called from an ACKNOWLEDGEMENT and from nowhere
    /// else — that is what makes the queue durable rather than best-effort, and
    /// it is why the ids reach the client at all (the envelope carries them).
    let clear (dbPath: string) (ids: int64 list) =
        if not ids.IsEmpty then
            use db = Db.openAt dbPath

            for id in ids do
                Db.executeWith db "DELETE FROM mailbox WHERE id = $id" [ "$id", box id ]
                |> ignore

    /// Drops note post nobody came back for. Called on sign-in rather than on a
    /// timer, because a server for two people should not need a scheduler.
    ///
    /// `AND channel = 0` is doing the load-bearing work in this statement.
    /// Without it, ageing would quietly delete unacknowledged messages and
    /// undo everything the acknowledgement protocol is for — the exact defect
    /// this module was rewritten to remove, reintroduced by a WHERE clause.
    let prune (dbPath: string) (maxAgeDays: float) =
        use db = Db.openAt dbPath
        let cutoff = DateTime.UtcNow.AddDays(-maxAgeDays).ToString "o"

        Db.executeWith
            db
            "DELETE FROM mailbox WHERE channel = 0 AND queued_at < $cutoff"
            [ "$cutoff", box cutoff ]

    let count (dbPath: string) (recipient: Handle) =
        use db = Db.openAt dbPath

        Db.query
            db
            "SELECT COUNT(*) FROM mailbox WHERE recipient = $recipient"
            [ "$recipient", box recipient.Folded ]
            (fun r -> r.GetInt32 0)
        |> List.head

    /// How much is waiting for somebody on one channel, which is what a refusal
    /// has to quote and what a test asserts against.
    let countOn (dbPath: string) (channel: Channel) (recipient: Handle) =
        use db = Db.openAt dbPath

        Db.query
            db
            "SELECT COUNT(*) FROM mailbox WHERE recipient = $recipient AND channel = $channel"
            [ "$recipient", box recipient.Folded; "$channel", box (ordinal channel) ]
            (fun r -> r.GetInt32 0)
        |> List.head
