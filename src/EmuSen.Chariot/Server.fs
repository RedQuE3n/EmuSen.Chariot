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
/// The passphrase is the server's front door and nothing more. It is the same
/// mechanism as a join code (`Crypto.deriveKey`) and carries the same fixed-salt
/// weakness described in `Pegasus_Sync.md` §5, in the EmuSen.Pegasus repository, so it decides who may open a
/// session with this server, not who they are. Identity is the signature.
///
/// WHAT CHARIOT MAY AND MAY NOT OPEN is decided by the envelope, and this is the
/// hinge of the whole design. A `Direct` frame is addressed to Chariot itself -
/// signing in, the roster - and is sealed under the server key, so Chariot reads
/// it. A `ToHandle` frame is somebody's note traffic, sealed under a join code
/// Chariot does not have, and it is moved without ever being decoded. Routing
/// arrives in the next pass; the refusal to decode is already true here.
type Server(port: int, passphrase: string, dbPath: string, ?queueLimit: int, ?maxAgeDays: float) =
    let key = Crypto.deriveKey passphrase
    let queueLimit = defaultArg queueLimit Mailbox.DefaultLimit
    let maxAgeDays = defaultArg maxAgeDays Mailbox.DefaultMaxAgeDays
    let listener = new TcpListener(IPAddress.Any, port)
    let presence = Presence()
    let signedIn = Event<PeerInfo>()
    let signedOut = Event<PeerInfo>()
    let refused = Event<string>()

    member _.Port = (listener.LocalEndpoint :?> IPEndPoint).Port
    member _.Presence = presence
    member _.SignedIn = signedIn.Publish
    member _.SignedOut = signedOut.Publish
    member _.Refused = refused.Publish
    member _.Start() = listener.Start()

    /// The sign-in exchange, which is deliberately the same one two peers use:
    /// Hello carrying a public key, a Challenge each way, a Proof each way.
    /// Reusing it means the thing standing between a stranger and the roster is
    /// code the Pegasus suite already exercises rather than a second
    /// implementation written for the server.
    ///
    /// Chariot does NOT prove itself to the client. A client's only assurance
    /// that it reached the right server is possession of the passphrase, so
    /// anybody holding that can impersonate this server. Stated as a limitation
    /// in docs/Chariot_Design.md §10 rather than left to be discovered.
    member private _.SignInAsync(stream: Stream, ct: CancellationToken) =
        task {
            let nonce = Crypto.newChallenge ()
            do! Framing.writeFrame stream key Direct (Challenge nonce) ct

            let mutable peer: PeerInfo option = None
            let mutable publicKey: byte[] option = None
            let mutable outcome: SignIn option = None

            while outcome.IsNone do
                let! envelope, frame = Framing.readFrame stream key ct

                if envelope <> Direct then
                    outcome <- Some(Rejected "a routed frame arrived before sign-in")
                else
                    match frame with
                    | Hello(who, offered, protocol) ->
                        if protocol <> Version.Protocol then
                            outcome <- Some(Rejected $"client speaks protocol {protocol}, this server speaks {Version.Protocol}")
                        else
                            match Accounts.register dbPath who offered with
                            | Refused(registered, presented) ->
                                outcome <-
                                    Some(
                                        Rejected
                                            $"{who.Handle.Value} is registered to key {registered.Value} and presented {presented.Value}"
                                    )
                            | Registered
                            | Known ->
                                peer <- Some who
                                publicKey <- Some offered

                    | Proof signature ->
                        match peer, publicKey with
                        | Some who, Some offered ->
                            match Attestation.verify offered who.Id nonce signature with
                            | Error why -> outcome <- Some(Rejected why)
                            | Ok() -> outcome <- Some(SignedIn who)
                        | _ -> outcome <- Some(Rejected "a proof arrived before the hello it belongs to")

                    | Challenge theirs ->
                        // Answered even though the client has no way to check
                        // it yet, so the exchange stays symmetric and the pass
                        // that gives Chariot an identity is a small one.
                        do! Framing.writeFrame stream key Direct (Proof(Crypto.respondToChallenge key theirs)) ct

                    | other -> outcome <- Some(Rejected $"expected a sign-in, got {other.GetType().Name}")

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
                    fun frame -> guarded (fun () -> Framing.writeFrame stream key Direct frame ct |> _.GetAwaiter().GetResult())
                  Forward =
                    fun envelope payload ->
                        guarded (fun () -> Framing.writeSealed stream envelope payload ct |> _.GetAwaiter().GetResult()) }

            try
                do! Handshake.asHost stream key ct
                let! outcome = this.SignInAsync(stream, ct)

                match outcome with
                | Rejected why -> refused.Trigger why
                | SignedIn peer ->
                    presence.Arrive(peer, wire) |> Option.iter (fun displaced -> displaced.Say Bye)
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
                                match Codec.decode (Crypto.openSealed key payload) with
                                | Bye -> raise (EndOfStreamException "client said goodbye")
                                | _ -> ()
                            | ToHandle destination -> this.Route(peer, destination, payload)
                            | FromHandle _ ->
                                // FromHandle is the relay's stamp on delivery.
                                // A client sending one is claiming to be this
                                // server about who sent something.
                                refused.Trigger $"{peer.Handle.Value} sent a FromHandle envelope, which is not a client's to write"
                    finally
                        if presence.Depart(peer, wire) then
                            signedOut.Trigger peer
                            presence.Broadcast()
            with
            | :? OperationCanceledException -> ()
            | :? EndOfStreamException -> ()
            | :? IOException -> ()
            | e -> refused.Trigger e.Message

            writeLock.Dispose()
        }

    /// Hands a payload to its destination, or holds it until that handle comes
    /// back. The payload is never opened on either path.
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
            match presence.WireFor destination with
            | Some recipient -> recipient.Forward (FromHandle sender.Handle) payload
            | None -> Mailbox.put dbPath queueLimit destination sender.Handle payload |> ignore

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
        member _.Dispose() = listener.Stop()
