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
