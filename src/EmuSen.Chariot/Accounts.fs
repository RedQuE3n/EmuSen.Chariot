namespace EmuSen.Chariot

open System
open EmuSen.Pegasus

/// What Chariot already believed about a handle claiming to sign in.
type Registration =
    /// This handle has never signed in here. The key is now recorded against it.
    | Registered
    /// The same key as last time.
    | Known
    /// The handle exists and this is not its key.
    | Refused of registered: PeerId * offered: PeerId

/// Accounts, server side: which public key owns which handle.
///
/// Trust on first use, for the same reason the desktop application uses it
/// between peers (`KnownPeers` there): there is no authority to ask, and this
/// project is deliberately not going to run one. The first key to claim a
/// handle owns it, and a later mismatch is refused rather than quietly
/// accepted or quietly replaced.
///
/// The weakness is the same one and worth restating rather than assuming a
/// reader carries it over: a handle claimed by an impostor BEFORE its rightful
/// owner ever connects is that impostor's handle from then on. Chariot cannot
/// tell the difference; nothing at this layer can. See docs/Chariot_Design.md §7.
module Accounts =

    /// Records a first registration and reports what was already known.
    ///
    /// The insert is attempted before the read, so "is this handle taken" and
    /// "take it" cannot interleave with another connection arriving between
    /// them. INSERT OR IGNORE reports the rows it wrote, which is precisely the
    /// first-registration answer, and it cannot overwrite an existing key -
    /// that would let a rejected impostor become the registered owner.
    let register (dbPath: string) (peer: PeerInfo) (publicKey: byte[]) =
        let offered = Fingerprint.ofPublicKey publicKey
        let now = DateTime.UtcNow.ToString "o"
        use db = Db.openAt dbPath

        let inserted =
            Db.executeWith
                db
                "INSERT OR IGNORE INTO accounts (handle, display, public_key, fingerprint, first_seen, last_seen)
                 VALUES ($handle, $display, $public, $fingerprint, $now, $now)"
                [ "$handle", box peer.Handle.Folded
                  "$display", box peer.Handle.Value
                  "$public", box publicKey
                  "$fingerprint", box offered.Value
                  "$now", box now ]

        if inserted = 1 then
            Registered
        else
            let registered =
                Db.query
                    db
                    "SELECT fingerprint FROM accounts WHERE handle = $handle"
                    [ "$handle", box peer.Handle.Folded ]
                    (fun r -> PeerId(r.GetString 0))

            match registered with
            | [ known ] when known = offered ->
                Db.executeWith
                    db
                    "UPDATE accounts SET last_seen = $now WHERE handle = $handle"
                    [ "$now", box now; "$handle", box peer.Handle.Folded ]
                |> ignore

                Known
            | [ known ] -> Refused(known, offered)
            // The insert was ignored, so a row exists. Not finding it means the
            // database changed underneath us, which is not a thing to guess at.
            | _ -> Refused(PeerId "unknown", offered)

    /// Every handle this server knows, for an operator who wants to look.
    let all (dbPath: string) =
        use db = Db.openAt dbPath

        Db.query db "SELECT display, fingerprint FROM accounts ORDER BY handle" [] (fun r ->
            r.GetString 0, PeerId(r.GetString 1))
        |> List.toArray

    /// Files a client's messaging key against the handle it has already proved
    /// it owns.
    ///
    /// THE CALLER MUST HAVE PROVED THE HANDLE FIRST and Server.fs is where that
    /// is enforced. This function cannot check it — it is handed a card and a
    /// row to write — so the guarantee lives at the one call site rather than
    /// being re-derived here. Accepting a card from an unproven connection would
    /// let anybody overwrite anybody's messaging key by claiming their name,
    /// which is the entire attack a key directory has to survive.
    ///
    /// The identity key in the card is NOT written. It is already in the row,
    /// put there by `register` from the key that was actually used to sign the
    /// challenge, and taking a second copy from a frame would be storing an
    /// assertion beside a fact — with the obvious failure mode that a later
    /// reader picks the wrong one. What is served back out is the registered
    /// key and the messaging key beside it.
    ///
    /// UPDATE rather than INSERT OR REPLACE: the row exists by the time this is
    /// reached, and a REPLACE would quietly recreate an account whose row had
    /// gone, losing the first_seen that trust on first use rests on.
    let publishCard (dbPath: string) (handle: Handle) (card: Card) =
        use db = Db.openAt dbPath

        Db.executeWith
            db
            "UPDATE accounts SET message_key = $key, message_signature = $signature WHERE handle = $handle"
            [ "$key", box card.Messaging
              "$signature", box card.Signature
              "$handle", box handle.Folded ]
        |> ignore

    /// The card for a handle, if that handle has ever published one.
    ///
    /// None covers both "no such account" and "an account that has never
    /// signed in from a build that has messaging", and the caller answers both
    /// with the same Unknown frame. Distinguishing them would tell a stranger
    /// which handles exist on this server, which is a question a relay should
    /// not be helping anybody enumerate.
    let cardFor (dbPath: string) (handle: Handle) =
        use db = Db.openAt dbPath

        Db.query
            db
            "SELECT display, public_key, message_key, message_signature FROM accounts
             WHERE handle = $handle AND message_key IS NOT NULL AND message_signature IS NOT NULL"
            [ "$handle", box handle.Folded ]
            (fun r ->
                { Handle = Handle.Parse(r.GetString 0)
                  Identity = r.GetFieldValue<byte[]> 1
                  Messaging = r.GetFieldValue<byte[]> 2
                  Signature = r.GetFieldValue<byte[]> 3 })
        |> List.tryHead
