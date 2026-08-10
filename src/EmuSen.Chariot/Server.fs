namespace EmuSen.Chariot

open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Threading
open EmuSen.Pegasus

/// How a client identified itself, or why it was turned away.
type SignIn =
    | SignedIn of PeerInfo
    | Rejected of why: string

/// Chariot's listener: accept a connection, make it prove who it is, and put it
/// on the roster.
///
/// The passphrase is the server's front door AND NOTHING MORE, which is now
/// true rather than aspirational. It is the same mechanism as a join code
/// (`Crypto.deriveKey`) and carries the same fixed-salt weakness described in
/// `Pegasus_Sync.md` §5, in the EmuSen.Pegasus repository, so it decides who may
/// open a session with this server, not who they are. It seals the sign-in
/// exchange, because that exchange has to be sealed under something both ends
/// already share — and then both ends agree an ephemeral key and everything
/// after is sealed under that. Before, every client held the key to every other
/// client's roster.
///
/// WHAT CHARIOT MAY AND MAY NOT OPEN is decided by the envelope, and this is the
/// hinge of the whole design. A `Direct` frame is addressed to Chariot itself -
/// signing in, the roster - and is sealed under a key Chariot holds, so Chariot
/// reads it. A `ToHandle` frame is somebody's note traffic, sealed under a join
/// code Chariot does not have, and it is moved without ever being decoded. The
/// key agreement changes which key the first kind uses and touches the second
/// not at all: wrapping note traffic in a second key agreed WITH this server
/// would be strictly worse than leaving it alone.
type Server(port: int, passphrase: string, dbPath: string, ?queueLimit: int, ?maxAgeDays: float, ?handle: Handle) =
    /// The doorbell, and what the sign-in exchange itself is sealed under.
    let doorKey = Crypto.deriveKey passphrase

    let queueLimit = defaultArg queueLimit Mailbox.DefaultLimit
    let maxAgeDays = defaultArg maxAgeDays Mailbox.DefaultMaxAgeDays
    let listener = new TcpListener(IPAddress.Any, port)
    let presence = Presence()
    let signedIn = Event<PeerInfo>()
    let signedOut = Event<PeerInfo>()
    let refused = Event<string>()

    /// Loaded once, at construction, because a server that cannot prove who it
    /// is has nothing to offer and should say so before it opens a port rather
    /// than on the first client. `ServerIdentity` explains why the private half
    /// is stored the way it is.
    let identity =
        match ServerIdentity.loadOrCreate dbPath (defaultArg handle ServerIdentity.defaultHandle) with
        | Ok loaded -> loaded
        | Error why -> failwith why

    member _.Port = (listener.LocalEndpoint :?> IPEndPoint).Port
    member _.Presence = presence
    member _.SignedIn = signedIn.Publish
    member _.SignedOut = signedOut.Publish
    member _.Refused = refused.Publish

    /// What a client pins. Shown on startup so an operator can read it aloud to
    /// somebody whose client is refusing to connect, which is the only way to
    /// tell "the server was rebuilt" from "this is not the server".
    member _.Identity = identity.Peer

    member _.Start() = listener.Start()

    /// The sign-in exchange: both ends prove who they are, and then agree a key
    /// nobody holding the passphrase can derive.
    ///
    /// It is deliberately the same exchange two peers use — Hello carrying a
    /// public key, a Challenge each way, a Proof each way — so the thing
    /// standing between a stranger and the roster is code the Pegasus suite
    /// already exercises rather than a second implementation written for a
    /// server. What is added is the Agree pair at the end.
    ///
    /// CHARIOT NOW PROVES ITSELF, which it did not before. It used to answer a
    /// client's Challenge with an HMAC over the passphrase key — a proof that it
    /// held the passphrase, which every client holds, and therefore a proof of
    /// nothing about who it was. It now signs with its own key, and the client
    /// pins that key on first sight and refuses a change afterwards. Anybody
    /// holding the passphrase could previously stand up a server and be
    /// believed; docs/Chariot_Design.md §7.1.
    ///
    /// The Agree frames go under the door key, because the session key is the
    /// thing they produce, and `controlKey` is replaced the moment the client's
    /// arrives. Nothing derives a key before the far side has PROVED itself: an
    /// ephemeral signed by an unproven identity is an ephemeral signed by
    /// anybody, which is unauthenticated Diffie-Hellman and no better than the
    /// passphrase it replaces.
    member private _.SignInAsync(stream: Stream, controlKey: byte[] ref, ct: CancellationToken) =
        task {
            use ephemeral = new Agreement.Ephemeral()
            let ourNonce = Crypto.newChallenge ()

            let sayUnderDoor frame =
                Framing.writeFrame stream doorKey Direct frame ct

            do! sayUnderDoor (Hello(identity.Peer, identity.PublicKey, Version.Protocol))
            do! sayUnderDoor (Challenge ourNonce)

            let mutable peer: PeerInfo option = None
            let mutable publicKey: byte[] option = None
            let mutable theirNonce: byte[] option = None
            let mutable proven = false
            let mutable offered = false
            let mutable outcome: SignIn option = None

            while outcome.IsNone do
                let! envelope, frame = Framing.readFrame stream doorKey ct

                if envelope <> Direct then
                    outcome <- Some(Rejected "a routed frame arrived before sign-in")
                else
                    match frame with
                    | Hello(who, presented, protocol) ->
                        if protocol <> Version.Protocol then
                            outcome <- Some(Rejected $"client speaks protocol {protocol}, this server speaks {Version.Protocol}")
                        else
                            match Accounts.register dbPath who presented with
                            | Refused(registered, presented) ->
                                outcome <-
                                    Some(
                                        Rejected
                                            $"{who.Handle.Value} is registered to key {registered.Value} and presented {presented.Value}"
                                    )
                            | Registered
                            | Known ->
                                peer <- Some who
                                publicKey <- Some presented

                    | Proof signature ->
                        match peer, publicKey with
                        | Some who, Some presented ->
                            match Attestation.verify presented who.Id ourNonce signature with
                            | Error why -> outcome <- Some(Rejected why)
                            | Ok() -> proven <- true
                        | _ -> outcome <- Some(Rejected "a proof arrived before the hello it belongs to")

                    | Challenge theirs ->
                        theirNonce <- Some theirs
                        do! sayUnderDoor (Proof(Attestation.prove identity theirs))

                    | Agree(theirs, signature) ->
                        match peer, publicKey, theirNonce with
                        | Some who, Some presented, Some nonce when proven ->
                            let salt = Agreement.salt ourNonce nonce

                            match ephemeral.Accept(presented, theirs, signature, ourNonce, salt) with
                            | Error why -> outcome <- Some(Rejected why)
                            | Ok agreed ->
                                controlKey.Value <- agreed
                                outcome <- Some(SignedIn who)
                        | _ -> outcome <- Some(Rejected "a key agreement arrived before the identity it belongs to")

                    | other -> outcome <- Some(Rejected $"expected a sign-in, got {other.GetType().Name}")

                    // Offered once the client has proved itself and has told us
                    // which nonce it wants us to sign over. Held back until the
                    // proof for the reason above.
                    if outcome.IsNone && proven && theirNonce.IsSome && not offered then
                        offered <- true
                        do! sayUnderDoor (ephemeral.Offer(identity, theirNonce.Value))

            return outcome.Value
        }

    /// Serves one client until it goes away. Everything it may do before
    /// signing in is in SignInAsync; everything after is the roster, and in the
    /// next pass, post.
    member private this.ServeAsync(client: TcpClient, ct: CancellationToken) =
        task {
            use client = client
            use stream = client.GetStream()
            let writeLock = new SemaphoreSlim(1, 1)

            /// What this connection's Direct traffic is sealed under. A ref
            /// rather than a mutable local because the wire below closes over
            /// it, and it starts as the door key because that is what the
            /// sign-in exchange has to be sealed under. One assignment, in
            /// SignInAsync, is the whole switch.
            let controlKey = ref doorKey

            // Blocking rather than fire-and-forget, and shared by both channels:
            // a roster broadcast racing a forwarded payload would interleave two
            // frames on one socket and neither would decode.
            let guarded write =
                writeLock.Wait()

                try
                    try
                        write ()
                    with _ ->
                        // A peer that has gone away is not an error worth
                        // raising from inside somebody else's broadcast.
                        ()
                finally
                    writeLock.Release() |> ignore

            let wire =
                { Say =
                    fun frame ->
                        guarded (fun () -> Framing.writeFrame stream controlKey.Value Direct frame ct |> _.GetAwaiter().GetResult())
                  Forward =
                    fun envelope payload ->
                        guarded (fun () -> Framing.writeSealed stream envelope payload ct |> _.GetAwaiter().GetResult()) }

            try
                do! Handshake.asHost stream doorKey ct
                let! outcome = this.SignInAsync(stream, controlKey, ct)

                match outcome with
                | Rejected why -> refused.Trigger why
                | SignedIn peer ->
                    // No displacement any more: a laptop signing in does not
                    // knock a desktop off. The token is what a departure names,
                    // because a handle no longer identifies one connection.
                    let token = presence.Arrive(peer, wire)
                    signedIn.Trigger peer
                    presence.Broadcast()
                    this.Deliver(peer.Handle, wire)

                    try
                        // Nothing to do but wait: the roster is pushed, and
                        // routing is the next pass. A read that ends is a
                        // client that has gone.
                        // readSealed, not readFrame. A ToHandle payload is
                        // sealed under a join code this server does not have,
                        // so decoding every frame that arrives would fail on
                        // exactly the traffic it exists to carry.
                        while not ct.IsCancellationRequested do
                            let! envelope, payload = Framing.readSealed stream ct

                            match envelope with
                            | Direct ->
                                match Codec.decode (Crypto.openSealed controlKey.Value payload) with
                                | Bye -> raise (EndOfStreamException "client said goodbye")
                                | _ -> ()
                            | ToHandle destination -> this.Route(peer, destination, payload)
                            | FromHandle _ ->
                                // FromHandle is the relay's stamp on delivery.
                                // A client sending one is claiming to be this
                                // server about who sent something.
                                refused.Trigger $"{peer.Handle.Value} sent a FromHandle envelope, which is not a client's to write"
                    finally
                        // Depart says whether that was this person's LAST
                        // connection. Announcing a departure when somebody has
                        // merely closed one of two laptops would take them out
                        // of everybody's buddy list while they are still there.
                        if presence.Depart token then
                            signedOut.Trigger peer

                        presence.Broadcast()
            with
            | :? OperationCanceledException -> ()
            | :? EndOfStreamException -> ()
            | :? IOException -> ()
            | e -> refused.Trigger e.Message

            writeLock.Dispose()
        }

    /// Hands a payload to every place its destination is signed in, or holds it
    /// until that handle comes back. The payload is never opened on either path.
    ///
    /// TO EVERY PLACE, and that is the routing half of keying presence by
    /// connection. A payload handed to somebody's laptop is not delivered to
    /// their desktop, so both get it. Over-delivering costs nothing, because
    /// Yjs updates are idempotent — docs/Chariot_Design.md §6 — which is the
    /// same property that let the mailbox skip acknowledgements entirely.
    ///
    /// What this does NOT do is queue for the devices that are offline while
    /// another one is on. Correctness there comes from SyncStep1 on the next
    /// conversation rather than from this queue, which is liveness — §6.1.
    ///
    /// An unregistered destination is refused rather than queued. Queueing for a
    /// handle nobody has ever signed in as would let any client fill the disk by
    /// posting to names it invented.
    member private _.Route(sender: PeerInfo, destination: Handle, payload: byte[]) =
        let known =
            Accounts.all dbPath |> Array.exists (fun (handle, _) -> Handle.Parse(handle).Folded = destination.Folded)

        if not known then
            refused.Trigger $"{sender.Handle.Value} addressed {destination.Value}, which has never signed in here"
        else
            match presence.WiresFor destination with
            | [||] -> Mailbox.put dbPath queueLimit destination sender.Handle payload |> ignore
            | recipients ->
                for recipient in recipients do
                    recipient.Forward (FromHandle sender.Handle) payload

    /// Hands over everything held for a handle that has just arrived.
    ///
    /// Cleared by id rather than by recipient, so post that arrives while this
    /// is running is not deleted undelivered.
    member private _.Deliver(handle: Handle, wire: Wire) =
        Mailbox.prune dbPath maxAgeDays |> ignore
        let waiting = Mailbox.peek dbPath handle

        for post in waiting do
            wire.Forward (FromHandle post.Sender) post.Payload

        Mailbox.clear dbPath (waiting |> List.map _.Id)

    /// Accepts for as long as the token allows. Every client is served on its
    /// own task, because a server that accepted one at a time would be a server
    /// two people cannot both use.
    member this.RunAsync(ct: CancellationToken) =
        task {
            while not ct.IsCancellationRequested do
                let! client = listener.AcceptTcpClientAsync ct
                client.NoDelay <- true
                this.ServeAsync(client, ct) |> ignore
        }

    interface IDisposable with
        member _.Dispose() =
            listener.Stop()
            (identity :> IDisposable).Dispose()
