namespace EmuSen.Chariot

open System
open EmuSen.Pegasus

/// One piece of undelivered post.
type Post =
    { Id: int64
      Sender: Handle
      Payload: byte[] }

/// Sealed payloads held for somebody who was not connected.
///
/// Every hard part of a message queue is absent here, and none of that is
/// cleverness on this end. `Pegasus_Sync.md` §4, in the EmuSen.Pegasus
/// repository, establishes that the payloads are Yjs updates and that applying
/// them is idempotent and order-independent. So there is no ordering to get
/// right, no exactly-once to enforce, no acknowledgement protocol and no
/// deduplication: hand over everything undelivered and be correct. Delivering
/// the same blob twice costs the recipient nothing, which is the property pass 7
/// will lean on when one handle can be signed in from two machines.
///
/// **This queue is liveness, not durability**, and the distinction is what makes
/// a bound safe in a project whose founding constraint is that no party may lose
/// information. Every peer keeps a complete replica on its own disk, so a sender
/// that handed work to Chariot has not given it away. Dropping queued post costs
/// promptness — the two replicas converge the next time both are online — and not
/// content. Losing content would take the sender's disk failing too, which is a
/// different problem that no queue fixes. See docs/Chariot_Design.md §6.1.
module Mailbox =

    /// Kept per recipient rather than globally, so one busy correspondent
    /// cannot evict everybody else's post.
    [<Literal>]
    let DefaultLimit = 512

    /// Post older than this is not worth delivering: a peer that has been away
    /// a fortnight will resynchronise from a state vector on reconnect anyway,
    /// which is cheaper than replaying a fortnight of deltas.
    [<Literal>]
    let DefaultMaxAgeDays = 14.0

    /// Queues one payload, then trims the recipient's queue back to the limit.
    ///
    /// Oldest first, because a Yjs update carries operations that later updates
    /// do not repeat, so the newest are the ones a recipient is most likely to
    /// still need in isolation — and because the alternative, refusing to
    /// accept new post once full, would make a full queue permanently full.
    let put (dbPath: string) (limit: int) (recipient: Handle) (sender: Handle) (payload: byte[]) =
        use db = Db.openAt dbPath

        Db.executeWith
            db
            "INSERT INTO mailbox (recipient, sender, payload, queued_at)
             VALUES ($recipient, $sender, $payload, $now)"
            [ "$recipient", box recipient.Folded
              "$sender", box sender.Folded
              "$payload", box payload
              "$now", box (DateTime.UtcNow.ToString "o") ]
        |> ignore

        Db.executeWith
            db
            "DELETE FROM mailbox WHERE id IN (
                 SELECT id FROM mailbox WHERE recipient = $recipient
                 ORDER BY id DESC LIMIT -1 OFFSET $limit)"
            [ "$recipient", box recipient.Folded; "$limit", box limit ]

    /// Everything held for a recipient, oldest first.
    let peek (dbPath: string) (recipient: Handle) =
        use db = Db.openAt dbPath

        Db.query
            db
            "SELECT id, sender, payload FROM mailbox WHERE recipient = $recipient ORDER BY id"
            [ "$recipient", box recipient.Folded ]
            (fun r ->
                { Id = r.GetInt64 0
                  Sender = Handle.Parse(r.GetString 1)
                  Payload = r.GetFieldValue<byte[]> 2 })

    /// Removes post that has been handed over.
    ///
    /// Taken by id rather than "everything for this recipient", so post that
    /// arrived while the drain was running is not deleted undelivered.
    let clear (dbPath: string) (ids: int64 list) =
        if not ids.IsEmpty then
            use db = Db.openAt dbPath

            for id in ids do
                Db.executeWith db "DELETE FROM mailbox WHERE id = $id" [ "$id", box id ]
                |> ignore

    /// Drops post nobody came back for. Called on sign-in rather than on a
    /// timer, because a server for two people should not need a scheduler.
    let prune (dbPath: string) (maxAgeDays: float) =
        use db = Db.openAt dbPath
        let cutoff = DateTime.UtcNow.AddDays(-maxAgeDays).ToString "o"
        Db.executeWith db "DELETE FROM mailbox WHERE queued_at < $cutoff" [ "$cutoff", box cutoff ]

    let count (dbPath: string) (recipient: Handle) =
        use db = Db.openAt dbPath

        Db.query
            db
            "SELECT COUNT(*) FROM mailbox WHERE recipient = $recipient"
            [ "$recipient", box recipient.Folded ]
            (fun r -> r.GetInt32 0)
        |> List.head
