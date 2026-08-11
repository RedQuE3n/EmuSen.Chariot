module EmuSen.Chariot.Tests.Clients

open System
open System.IO
open System.Net.Sockets
open System.Threading
open System.Threading.Tasks
open EmuSen.Pegasus

// A read that never returns is the one failure mode a harness must not have,
// and every read below used to have it. They were passed CancellationToken.None,
// and the deadline loops wrapping them only look at the clock BETWEEN reads - so
// a server that said nothing at all blocked forever rather than failing, and
// took the whole suite with it. Every `timeoutMs` argument in this file did
// nothing in precisely the case it exists for: it fires when frames keep
// arriving and the wanted one does not, never when the socket goes quiet.
//
// 0.4.0 is what made that reachable. The envelope changed shape - a routed
// frame names its channel, a delivery names the post it came out of - so a test
// waiting on a frame the new server no longer sends waits forever. `dotnet test`
// wedged for 10m20s in CI and past 300s locally and reported NOTHING either
// time: a suite that could neither pass nor fail, which is worse than a red one
// because a red one tells you where to look.
//
// Every read and write now runs under a token with a hard ceiling. It is far
// longer than any exchange here needs, deliberately - its job is to turn a hang
// into a named failure, not to police latency. The per-call deadlines still do
// that, and they finally work, because the read underneath them can now return.
let private ceiling = TimeSpan.FromSeconds 20.0

let private await (work: CancellationToken -> Task<'a>) : 'a =
    use cts = new CancellationTokenSource(ceiling)

    try
        work cts.Token |> _.GetAwaiter().GetResult()
    with :? OperationCanceledException ->
        failwith $"the socket went quiet for {ceiling.TotalSeconds}s: no frame arrived"

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
    do await (Handshake.asJoiner stream doorKey)

    let mutable server: PeerInfo option = None
    let mutable serverKey: byte[] option = None

    let read () = await (Framing.readFrame stream key)

    let write frame =
        await (Framing.writeFrame stream key Direct frame)

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
                match await (Framing.readSealed stream) with
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
        await (Framing.writeSealed stream (ToHandle(destination, NoteTraffic)) sealedBytes)

    /// Writes an envelope a client has no business writing, for the tests that
    /// check the server says so.
    member _.Forge(envelope: Envelope, joinKey: byte[], frame: Frame) =
        let sealedBytes = Crypto.seal joinKey (Codec.encode frame)
        await (Framing.writeSealed stream envelope sealedBytes)

    /// Publishes this client's card, which is what makes it reachable by
    /// message at all.
    member _.PublishCard() = write (Card(Messaging.cardOf identity))

    /// Asks for somebody's card and waits for the answer.
    ///
    /// Returns the frame rather than the card, because "there is no such card"
    /// is one of the two answers and a test has to be able to assert it.
    member this.AskFor(who: Handle, timeoutMs: int) =
        write (Ask who)
        let deadline = DateTime.UtcNow.AddMilliseconds(float timeoutMs)

        let rec next () =
            if DateTime.UtcNow > deadline then
                failwith "the server never answered the ask"
            else
                match Codec.decode (Crypto.openSealed key (this.NextSealedDirect timeoutMs)) with
                | Card card -> Card card
                | Unknown handle -> Unknown handle
                | _ -> next ()

        next ()

    /// Sends a real sealed message to somebody, the way the application does.
    member _.MessageTo(recipient: Handle, theirMessagingKey: byte[], body: string) =
        let id = MessageId.New()

        let plain =
            Codec.encode (Message(id, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), body))

        let sealedBytes = Messaging.seal identity theirMessagingKey plain
        await (Framing.writeSealed stream (ToHandle(recipient, MessageTraffic)) sealedBytes)

        id

    /// Waits for a delivered message, opens it, and hands back the post id as
    /// well as the text — the id being what the recipient must acknowledge.
    member _.NextMessage(sender: byte[], timeoutMs: int) =
        let deadline = DateTime.UtcNow.AddMilliseconds(float timeoutMs)

        let rec next () =
            if DateTime.UtcNow > deadline then
                failwith "no message was delivered"
            else
                match await (Framing.readSealed stream) with
                | FromHandle(who, MessageTraffic, post), payload ->
                    match Messaging.tryOpen identity sender payload with
                    | Some plain ->
                        match Codec.decode plain with
                        | Message(id, _, body) -> who, post, id, body
                        | other -> failwith $"expected a message, got {other.GetType().Name}"
                    | None -> failwith "a delivered message did not open"
                | _ -> next ()

        next ()

    /// The refusal a sender is told about, for the tests that check a message
    /// which did not go anywhere says so.
    member this.NextUndeliverable(timeoutMs: int) =
        let deadline = DateTime.UtcNow.AddMilliseconds(float timeoutMs)

        let rec next () =
            if DateTime.UtcNow > deadline then
                failwith "the server never said it could not deliver"
            else
                match Codec.decode (Crypto.openSealed key (this.NextSealedDirect timeoutMs)) with
                | Undeliverable(who, why) -> who, why
                | _ -> next ()

        next ()

    member _.Acknowledge(posts: int64[]) = write (Ack posts)

    /// Waits for a forwarded payload and opens it with the join code, which is
    /// the whole point: Chariot moved something it could not read.
    member _.NextDelivery(joinKey: byte[], timeoutMs: int) =
        let deadline = DateTime.UtcNow.AddMilliseconds(float timeoutMs)

        let rec next () =
            if DateTime.UtcNow > deadline then
                failwith "nothing was delivered"
            else
                match await (Framing.readSealed stream) with
                | FromHandle(sender, _, _), payload -> sender, Codec.decode (Crypto.openSealed joinKey payload)
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
