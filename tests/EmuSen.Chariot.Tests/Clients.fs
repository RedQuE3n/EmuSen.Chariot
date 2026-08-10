module EmuSen.Chariot.Tests.Clients

open System
open System.IO
open System.Net.Sockets
open System.Threading
open EmuSen.Pegasus

let ct = CancellationToken.None

/// A client of Chariot, driven by hand.
///
/// Deliberately not a shared library with the desktop application: the point of
/// these tests is that a client built only from EmuSen.Pegasus.Core and the
/// documented exchange can sign in. If this needed anything from the Pegasus
/// application, the protocol would not be the contract it claims to be.
type Client(host: string, port: int, passphrase: string, identity: Identity) =
    let doorKey = Crypto.deriveKey passphrase

    /// Whichever key this connection's Direct traffic is currently sealed
    /// under: the door key through sign-in, the agreed session key afterwards.
    let mutable key = doorKey

    let client = new TcpClient()
    do client.Connect(host, port)
    do client.NoDelay <- true
    let stream = client.GetStream()
    do Handshake.asJoiner stream doorKey ct |> _.GetAwaiter().GetResult()

    let mutable server: PeerInfo option = None
    let mutable serverKey: byte[] option = None

    let read () =
        Framing.readFrame stream key ct |> _.GetAwaiter().GetResult()

    let write frame =
        Framing.writeFrame stream key Direct frame ct |> _.GetAwaiter().GetResult()

    member _.Identity = identity

    /// Who the server proved itself to be. None until SignIn has returned.
    member _.Server = server

    member _.ServerKey = serverKey

    /// The key agreed for this connection, so a test can show that what the
    /// server said opens under it and does not open under the passphrase.
    member _.SessionKey = key

    member _.Send(frame) = write frame

    /// The next thing the server said on its own account, still sealed. The
    /// point of not opening it here is that the test gets to choose which key
    /// to try.
    member _.NextSealedDirect(timeoutMs: int) =
        let deadline = DateTime.UtcNow.AddMilliseconds(float timeoutMs)

        let rec next () =
            if DateTime.UtcNow > deadline then
                failwith "the server said nothing"
            else
                match Framing.readSealed stream ct |> _.GetAwaiter().GetResult() with
                | Direct, payload -> payload
                | _ -> next ()

        next ()

    /// The exchange as documented, both directions.
    ///
    /// Written against Pegasus_Sync.md §4.3 rather than against Chariot's
    /// source, which is the point of this file existing at all: if the client
    /// and the server were one implementation, this suite would prove they
    /// agree with each other rather than that either agrees with the protocol.
    ///
    /// Verifies the server's proof for real. A client that skipped that check
    /// would make every server-side test pass against a server that signed
    /// nothing.
    member _.SignIn() =
        use ephemeral = new Agreement.Ephemeral()
        let ourNonce = Crypto.newChallenge ()
        let mutable theirNonce: byte[] option = None
        let mutable proven = false
        let mutable admitted = false

        while not admitted do
            match read () with
            | _, Hello(who, offered, protocol) ->
                if protocol <> Version.Protocol then
                    failwith $"server speaks protocol {protocol}, this client speaks {Version.Protocol}"

                server <- Some who
                serverKey <- Some offered

            | _, Challenge nonce ->
                theirNonce <- Some nonce
                write (Hello(identity.Peer, identity.PublicKey, Version.Protocol))
                write (Challenge ourNonce)
                write (Proof(Attestation.prove identity nonce))

            | _, Proof signature ->
                match server, serverKey with
                | Some who, Some offered ->
                    match Attestation.verify offered who.Id ourNonce signature with
                    | Ok() -> proven <- true
                    | Error why -> failwith why
                | _ -> failwith "the server proved itself before saying who it was"

            | _, Agree(theirs, signature) ->
                match serverKey, theirNonce with
                | Some offered, Some nonce when proven ->
                    match ephemeral.Accept(offered, theirs, signature, ourNonce, Agreement.salt nonce ourNonce) with
                    | Ok agreed ->
                        write (ephemeral.Offer(identity, nonce))
                        key <- agreed
                        admitted <- true
                    | Error why -> failwith why
                | _ -> failwith "the server offered a key agreement before proving who it was"

            | _ -> ()

    /// For the tests where the server is SUPPOSED to refuse.
    ///
    /// Sign-in is a conversation now rather than two writes, so a client the
    /// server turns away finds the socket closed underneath it mid-exchange.
    /// That is the correct outcome and it is not an assertion failure, so it is
    /// swallowed here rather than in each test — but only here, so a refusal
    /// anywhere else still fails loudly.
    member this.TrySignIn() =
        try
            this.SignIn()
            true
        with _ ->
            false

    /// Waits for the next roster, so a test asserts on a push rather than on a
    /// sleep. Returns the handles, sorted, because that is what a buddy list is.
    member _.NextRoster(timeoutMs: int) =
        let deadline = DateTime.UtcNow.AddMilliseconds(float timeoutMs)

        let rec next () =
            if DateTime.UtcNow > deadline then
                failwith "no roster arrived"
            else
                match read () with
                | _, Roster peers -> peers |> Array.map (fun p -> p.Handle.Value)
                | _ -> next ()

        next ()

    /// Sends a payload addressed to somebody else, sealed under a key this
    /// server has no way to derive. What comes back on the far side must be
    /// these exact bytes.
    member _.PostTo(destination: Handle, joinKey: byte[], frame: Frame) =
        let sealedBytes = Crypto.seal joinKey (Codec.encode frame)
        Framing.writeSealed stream (ToHandle destination) sealedBytes ct |> _.GetAwaiter().GetResult()

    /// Writes an envelope a client has no business writing, for the tests that
    /// check the server says so.
    member _.Forge(envelope: Envelope, joinKey: byte[], frame: Frame) =
        let sealedBytes = Crypto.seal joinKey (Codec.encode frame)
        Framing.writeSealed stream envelope sealedBytes ct |> _.GetAwaiter().GetResult()

    /// Waits for a forwarded payload and opens it with the join code, which is
    /// the whole point: Chariot moved something it could not read.
    member _.NextDelivery(joinKey: byte[], timeoutMs: int) =
        let deadline = DateTime.UtcNow.AddMilliseconds(float timeoutMs)

        let rec next () =
            if DateTime.UtcNow > deadline then
                failwith "nothing was delivered"
            else
                match Framing.readSealed stream ct |> _.GetAwaiter().GetResult() with
                | FromHandle sender, payload -> sender, Codec.decode (Crypto.openSealed joinKey payload)
                | _ -> next ()

        next ()

    interface IDisposable with
        member _.Dispose() =
            try
                stream.Dispose()
                client.Dispose()
            with _ ->
                ()

let identity name = Identity.Generate(Handle.Parse name)

let tempDb () =
    let dir = Path.Combine(Path.GetTempPath(), "chariot-tests", Guid.NewGuid().ToString "N")
    Directory.CreateDirectory dir |> ignore
    Path.Combine(dir, "chariot.db")
