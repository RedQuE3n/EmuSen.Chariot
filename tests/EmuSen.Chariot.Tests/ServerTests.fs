module EmuSen.Chariot.Tests.ServerTests

open System
open System.Threading
open Xunit
open EmuSen.Pegasus
open EmuSen.Chariot
open EmuSen.Chariot.Tests.Clients

[<Literal>]
let private Passphrase = "a-server-passphrase"

/// A running server on an operating-system-chosen port, with its own database.
type private Running(?queueLimit: int) =
    let cts = new CancellationTokenSource()
    let db = tempDb ()
    let server = new Server(0, Passphrase, db, ?queueLimit = queueLimit)
    do server.Start()
    do server.RunAsync cts.Token |> ignore

    member _.Port = server.Port
    member _.Db = db
    member _.Server = server

    member _.Connect(identity: Identity) =
        new Client("127.0.0.1", server.Port, Passphrase, identity)

    interface IDisposable with
        member _.Dispose() =
            cts.Cancel()
            (server :> IDisposable).Dispose()

let private waitFor (timeoutMs: int) (condition: unit -> bool) =
    let deadline = DateTime.UtcNow.AddMilliseconds(float timeoutMs)

    while not (condition ()) && DateTime.UtcNow < deadline do
        Thread.Sleep 10

    condition ()

[<Fact>]
let ``a client that proves itself is signed in`` () =
    use running = new Running()
    use alice = identity "alice"
    let admitted = ResizeArray<PeerInfo>()
    running.Server.SignedIn.Add admitted.Add

    use client = running.Connect alice
    client.SignIn()

    Assert.True(waitFor 5000 (fun () -> admitted.Count = 1), "nobody was signed in")
    Assert.Equal("alice", admitted[0].Handle.Value)
    Assert.Equal(alice.Fingerprint, admitted[0].Id)

[<Fact>]
let ``two clients see each other appear`` () =
    // The pass's own acceptance test. A buddy list is only a buddy list if the
    // other person shows up in it without being asked for.
    use running = new Running()
    use aliceId = identity "alice"
    use bobId = identity "bob"

    use alice = running.Connect aliceId
    alice.SignIn()
    Assert.Empty(alice.NextRoster 5000)

    use bob = running.Connect bobId
    bob.SignIn()

    // Alice is told about Bob without having asked, which is the whole point.
    Assert.Equal<string[]>([| "bob" |], alice.NextRoster 5000)
    Assert.Equal<string[]>([| "alice" |], bob.NextRoster 5000)

[<Fact>]
let ``a roster never contains the person reading it`` () =
    use running = new Running()
    use aliceId = identity "alice"
    use alice = running.Connect aliceId
    alice.SignIn()

    Assert.DoesNotContain("alice", alice.NextRoster 5000)

[<Fact>]
let ``a client that leaves disappears from everyone else's roster`` () =
    use running = new Running()
    use aliceId = identity "alice"
    use bobId = identity "bob"

    use alice = running.Connect aliceId
    alice.SignIn()
    alice.NextRoster 5000 |> ignore

    let bob = running.Connect bobId
    bob.SignIn()
    Assert.Equal<string[]>([| "bob" |], alice.NextRoster 5000)

    (bob :> IDisposable).Dispose()

    Assert.Empty(alice.NextRoster 5000)

[<Fact>]
let ``a client that never proves itself is not on the roster`` () =
    // Says hello, presents a key, and then simply does not sign the challenge.
    // Announcing that peer would put an unverified name in somebody's buddy
    // list, which is the thing a proof exists to prevent.
    use running = new Running()
    use aliceId = identity "alice"
    use impostorId = identity "bob"

    use alice = running.Connect aliceId
    alice.SignIn()
    alice.NextRoster 5000 |> ignore

    use silent = running.Connect impostorId
    silent.Send(Hello(impostorId.Peer, impostorId.PublicKey, Version.Protocol))

    Thread.Sleep 300
    Assert.Empty(running.Server.Presence.RosterFor aliceId.Peer)

[<Fact>]
let ``a second key claiming a registered handle is refused`` () =
    // Trust on first use, server side. Alice registers, then somebody else
    // arrives claiming to be Alice with their own key.
    use running = new Running()
    use aliceId = identity "alice"

    use alice = running.Connect aliceId
    alice.SignIn()
    Assert.True(waitFor 5000 (fun () -> Accounts.all(running.Db).Length = 1))

    let refusals = ResizeArray<string>()
    running.Server.Refused.Add refusals.Add

    use impostorId = Identity.Generate(Handle.Parse "alice")
    use impostor = running.Connect impostorId
    Assert.False(impostor.TrySignIn(), "an impostor got all the way in")

    Assert.True(waitFor 5000 (fun () -> refusals.Count > 0), "an impostor was not refused")
    Assert.Contains(aliceId.Fingerprint.Value, refusals[0])
    Assert.Contains(impostorId.Fingerprint.Value, refusals[0])
    Assert.Equal<string[]>([| "alice" |], running.Server.Presence.Everyone |> Array.map _.Handle.Value)

[<Fact>]
let ``a registered handle keeps its key across a restart`` () =
    // The accounts table is the part that has to outlive the process; presence
    // is deliberately the part that does not.
    let db = tempDb ()
    use aliceId = identity "alice"

    do
        use cts = new CancellationTokenSource()
        use first = new Server(0, Passphrase, db)
        first.Start()
        first.RunAsync cts.Token |> ignore
        use client = new Client("127.0.0.1", first.Port, Passphrase, aliceId)
        client.SignIn()
        Assert.True(waitFor 5000 (fun () -> Accounts.all(db).Length = 1))
        cts.Cancel()

    use cts = new CancellationTokenSource()
    use second = new Server(0, Passphrase, db)
    second.Start()
    second.RunAsync cts.Token |> ignore

    // Nobody is present on a fresh process, but the account survived.
    Assert.Empty second.Presence.Everyone
    Assert.Equal<(string * PeerId)[]>([| "alice", aliceId.Fingerprint |], Accounts.all db)

    let refusals = ResizeArray<string>()
    second.Refused.Add refusals.Add
    use impostorId = Identity.Generate(Handle.Parse "alice")
    use impostor = new Client("127.0.0.1", second.Port, Passphrase, impostorId)
    Assert.False(impostor.TrySignIn(), "an impostor got all the way in")

    Assert.True(waitFor 5000 (fun () -> refusals.Count > 0), "the restarted server forgot who owned the handle")

[<Fact>]
let ``a client with the wrong passphrase never reaches sign-in`` () =
    use running = new Running()
    use aliceId = identity "alice"

    Assert.ThrowsAny<exn>(fun () ->
        use wrong = new Client("127.0.0.1", running.Port, "not-the-passphrase", aliceId)
        wrong.SignIn())
    |> ignore

    Assert.Empty running.Server.Presence.Everyone

// ---------------------------------------------------------------------------
// The mailbox
// ---------------------------------------------------------------------------

/// The peers' own key. Chariot never sees this and cannot derive it.
let private joinKey = Crypto.deriveKey "7-lantern-quartz"

let private note text = Update(Text.Encoding.UTF8.GetBytes(text: string))

[<Fact>]
let ``a payload for a connected peer is handed straight over`` () =
    use running = new Running()
    use aliceId = identity "alice"
    use bobId = identity "bob"

    use alice = running.Connect aliceId
    alice.SignIn()
    use bob = running.Connect bobId
    bob.SignIn()
    Assert.True(waitFor 5000 (fun () -> running.Server.Presence.Everyone.Length = 2))

    alice.PostTo(bobId.Handle, joinKey, note "straight over")

    let sender, frame = bob.NextDelivery(joinKey, 5000)
    Assert.Equal(aliceId.Handle, sender)
    Assert.Equal(note "straight over", frame)
    Assert.Equal(0, Mailbox.count running.Db bobId.Handle)

[<Fact>]
let ``a payload for an absent peer waits, and arrives when they come back`` () =
    // The pass in one test. Bob has signed in here before, so he is a known
    // account, but he is not connected when Alice writes.
    use running = new Running()
    use aliceId = identity "alice"
    use bobId = identity "bob"

    do
        use bob = running.Connect bobId
        bob.SignIn()
        Assert.True(waitFor 5000 (fun () -> Accounts.all(running.Db).Length = 1))

    Assert.True(waitFor 5000 (fun () -> running.Server.Presence.Everyone.Length = 0))

    use alice = running.Connect aliceId
    alice.SignIn()
    alice.PostTo(bobId.Handle, joinKey, note "left for later")

    Assert.True(waitFor 5000 (fun () -> Mailbox.count running.Db bobId.Handle = 1), "nothing was queued")

    use bob = running.Connect bobId
    bob.SignIn()

    let sender, frame = bob.NextDelivery(joinKey, 5000)
    Assert.Equal(aliceId.Handle, sender)
    Assert.Equal(note "left for later", frame)

    // And it is not delivered twice on the next reconnect.
    Assert.True(waitFor 5000 (fun () -> Mailbox.count running.Db bobId.Handle = 0), "post survived delivery")

[<Fact>]
let ``queued post is stored sealed, and the server cannot read it`` () =
    // The end-to-end property, asserted against the database rather than the
    // wire: if Chariot could read what it holds, everything else is theatre.
    use running = new Running()
    use aliceId = identity "alice"
    use bobId = identity "bob"

    do
        use bob = running.Connect bobId
        bob.SignIn()
        Assert.True(waitFor 5000 (fun () -> Accounts.all(running.Db).Length = 1))

    // Waiting for the departure rather than assuming it. This test used to post
    // the moment Bob's socket was disposed, which is a race the sibling test
    // above already knew about: a payload for a connection the server has not
    // yet noticed is gone gets handed to a dying socket and dropped instead of
    // queued. It passed for as long as the timing happened to hold and stopped
    // when sign-in grew two round trips.
    Assert.True(waitFor 5000 (fun () -> running.Server.Presence.Everyone.Length = 0))

    use alice = running.Connect aliceId
    alice.SignIn()
    alice.PostTo(bobId.Handle, joinKey, note "nobody at this server may read this")
    Assert.True(waitFor 5000 (fun () -> Mailbox.count running.Db bobId.Handle = 1))

    let held = Mailbox.peek running.Db bobId.Handle |> List.head
    let asText = Text.Encoding.UTF8.GetString held.Payload
    Assert.DoesNotContain("nobody at this server", asText)

    // The server's own key does not open it; the peers' does.
    Assert.True((Crypto.tryOpenSealed (Crypto.deriveKey Passphrase) held.Payload).IsNone)
    Assert.Equal(note "nobody at this server may read this", Codec.decode (Crypto.openSealed joinKey held.Payload))

[<Fact>]
let ``the note queue is bounded, and drops the oldest`` () =
    // A bound is safe ON THE NOTE CHANNEL because that queue is liveness and not
    // durability: the sender still holds a complete replica, so what is lost is
    // promptness. §13 withdraws that argument for messages, and the test below
    // this one is the other half of the pair.
    use running = new Running(queueLimit = 3)
    use aliceId = identity "alice"
    use bobId = identity "bob"

    do
        use bob = running.Connect bobId
        bob.SignIn()
        Assert.True(waitFor 5000 (fun () -> Accounts.all(running.Db).Length = 1))

    // WAITS FOR BOB TO BE GONE, not merely for his socket to be closed, and
    // this line is the whole of a defect this test carried from the day it was
    // written. Closing a connection does not make the server think anybody has
    // left: it learns that when its own read loop notices the stream has ended,
    // which happens on the server's schedule and not on the test's. Post
    // addressed to somebody the server still believes is present is FORWARDED
    // rather than queued — into a socket nobody is reading — and the guarded
    // write swallows the failure, so the mailbox stays empty and the assertion
    // below reports a queue that was never filled rather than a bound that did
    // not hold.
    //
    // It passed for a year because the departure usually won the race. It began
    // failing about one run in five when messaging changed the timing around it,
    // which is the only reason anybody looked. Nothing about the server was
    // wrong either way. §13.3.
    Assert.True(
        waitFor 5000 (fun () ->
            running.Server.Presence.Everyone
            |> Array.forall (fun peer -> peer.Handle.Folded <> bobId.Handle.Folded)),
        "the server still believed bob was connected"
    )

    use alice = running.Connect aliceId
    alice.SignIn()

    for i in 1..6 do
        alice.PostTo(bobId.Handle, joinKey, note $"note {i}")

    // Waits on the CONTENT rather than on the count, for a second and smaller
    // reason: the queue passes through three items on its way to six, so
    // `count = 3` is true for a moment while notes 1, 2 and 3 are in it. A poll
    // landing there would read the wrong contents out of a queue that had not
    // finished filling. The state below only ever holds at the end.
    let kept () =
        Mailbox.peek running.Db bobId.Handle
        |> List.map (fun post -> Codec.decode (Crypto.openSealed joinKey post.Payload))

    let expected = [ note "note 4"; note "note 5"; note "note 6" ]

    Assert.True(waitFor 5000 (fun () -> kept () = expected), "the queue did not settle at the newest three")
    Assert.Equal(3, Mailbox.count running.Db bobId.Handle)

[<Fact>]
let ``post addressed to a handle that has never signed in is refused, not queued`` () =
    // Otherwise any client could fill the disk by writing to names it invented.
    use running = new Running()
    use aliceId = identity "alice"
    let refusals = ResizeArray<string>()
    running.Server.Refused.Add refusals.Add

    use alice = running.Connect aliceId
    alice.SignIn()
    alice.PostTo(Handle.Parse "nobody", joinKey, note "into the void")

    Assert.True(waitFor 5000 (fun () -> refusals.Count > 0), "an invented destination was accepted")
    Assert.Contains("never signed in", refusals[0])
    Assert.Equal(0, Mailbox.count running.Db (Handle.Parse "nobody"))

[<Fact>]
let ``a client may not stamp a delivery as coming from somebody`` () =
    // FromHandle is the relay's word about who sent something. A client writing
    // one is claiming to be the server.
    use running = new Running()
    use aliceId = identity "alice"
    let refusals = ResizeArray<string>()
    running.Server.Refused.Add refusals.Add

    use alice = running.Connect aliceId
    alice.SignIn()
    alice.Forge(FromHandle(Handle.Parse "bob", NoteTraffic, 0L), joinKey, note "pretending to be bob")

    Assert.True(waitFor 5000 (fun () -> refusals.Count > 0), "a forged FromHandle was accepted")
    Assert.Contains("not a client's to write", refusals[0])

// ---------------------------------------------------------------------------
// Pass 7: Chariot proves itself
// ---------------------------------------------------------------------------

[<Fact>]
let ``the server signs its own challenge with its own key`` () =
    // Client.SignIn verifies the server's proof for real and throws if it does
    // not hold, so getting through the exchange at all is the assertion. What
    // is checked here is that the key it proved is the one the server claims
    // and that the fingerprint matches, which is what a client will pin.
    use running = new Running()
    use aliceId = identity "alice"
    use alice = running.Connect aliceId
    alice.SignIn()

    Assert.Equal(Some "chariot", alice.Server |> Option.map _.Handle.Value)
    Assert.Equal(Some running.Server.Identity.Id, alice.Server |> Option.map _.Id)
    Assert.Equal(Some(Fingerprint.ofPublicKey alice.ServerKey.Value), alice.Server |> Option.map _.Id)

[<Fact>]
let ``the server keeps the same key across a restart`` () =
    // A client pins this key. A server that minted a new one on every start
    // would look to every one of its users like an impostor, every time.
    let db = tempDb ()
    use aliceId = identity "alice"

    let identityOf () =
        use cts = new CancellationTokenSource()
        use server = new Server(0, Passphrase, db)
        server.Start()
        server.RunAsync cts.Token |> ignore
        use client = new Client("127.0.0.1", server.Port, Passphrase, aliceId)
        client.SignIn()
        cts.Cancel()
        client.Server.Value, client.ServerKey.Value

    let firstPeer, firstKey = identityOf ()
    let secondPeer, secondKey = identityOf ()

    Assert.Equal(firstPeer.Id, secondPeer.Id)
    Assert.Equal<byte[]>(firstKey, secondKey)

[<Fact>]
let ``a database that belongs to another server is refused rather than renamed`` () =
    // Taking the new name would present the same key under a name nobody has
    // pinned; keeping the old one would silently ignore what the operator
    // asked for. Both are worse than stopping.
    let db = tempDb ()

    do
        use first = new Server(0, Passphrase, db)
        Assert.Equal("chariot", first.Identity.Handle.Value)

    let refused =
        Assert.ThrowsAny<exn>(fun () -> new Server(0, Passphrase, db, handle = Handle.Parse "somewhere-else") |> ignore)

    Assert.Contains("belongs to a server called chariot", refused.Message)

[<Fact>]
let ``holding the passphrase does not open what the server says afterwards`` () =
    // The passphrase is the doorbell. It seals the sign-in exchange, because
    // that exchange is what produces the session key, and nothing after it.
    use running = new Running()
    use aliceId = identity "alice"
    use bobId = identity "bob"

    use alice = running.Connect aliceId
    alice.SignIn()

    // A roster, pushed because somebody arrived.
    use bob = running.Connect bobId
    bob.SignIn()

    // Two of them: the empty roster Alice got on arrival, and the one naming
    // Bob. Both are asserted, because "the passphrase opens nothing after
    // sign-in" is a claim about every frame and not about a lucky one.
    let rosters =
        [ alice.NextSealedDirect 5000; alice.NextSealedDirect 5000 ]

    let door = Crypto.deriveKey Passphrase

    for payload in rosters do
        Assert.True((Crypto.tryOpenSealed door payload).IsNone, "the passphrase opened something said after sign-in")

    let named =
        rosters
        |> List.map (fun payload -> Codec.decode (Crypto.openSealed alice.SessionKey payload))
        |> List.collect (function
            | Roster peers -> peers |> Array.map _.Handle.Value |> List.ofArray
            | other -> failwith $"expected a roster, got {other.GetType().Name}")

    Assert.Equal<string list>([ "bob" ], named)

// ---------------------------------------------------------------------------
// Pass 7: one person, two places
// ---------------------------------------------------------------------------

[<Fact>]
let ``one person may be signed in from two places at once`` () =
    // Presence used to be keyed by handle, so a laptop signing in knocked the
    // desktop off without telling anybody. It is keyed by connection now, and
    // the de-duplication happens where it belongs: when a roster is built.
    use running = new Running()
    use aliceId = identity "alice"
    use bobId = identity "bob"

    use laptop = running.Connect bobId
    laptop.SignIn()
    use desktop = running.Connect bobId
    desktop.SignIn()

    Assert.True(waitFor 5000 (fun () -> running.Server.Presence.Connections = 2), "the second place displaced the first")

    // One person, not two, and not one per device.
    Assert.Equal<string[]>([| "bob" |], running.Server.Presence.Everyone |> Array.map _.Handle.Value)

    use alice = running.Connect aliceId
    alice.SignIn()
    Assert.Equal<string[]>([| "bob" |], alice.NextRoster 5000)

    // And post reaches both places, because a payload handed to a laptop is not
    // delivered to a desktop. Over-delivery is free: Yjs updates are idempotent.
    alice.PostTo(bobId.Handle, joinKey, note "to both places")

    let _, atLaptop = laptop.NextDelivery(joinKey, 5000)
    let _, atDesktop = desktop.NextDelivery(joinKey, 5000)
    Assert.Equal(note "to both places", atLaptop)
    Assert.Equal(note "to both places", atDesktop)

[<Fact>]
let ``closing one device leaves the person present at the other`` () =
    use running = new Running()
    use bobId = identity "bob"
    let departures = ResizeArray<PeerInfo>()
    running.Server.SignedOut.Add departures.Add

    let laptop = running.Connect bobId
    laptop.SignIn()
    use desktop = running.Connect bobId
    desktop.SignIn()
    Assert.True(waitFor 5000 (fun () -> running.Server.Presence.Connections = 2))

    (laptop :> IDisposable).Dispose()
    Assert.True(waitFor 5000 (fun () -> running.Server.Presence.Connections = 1), "the connection was not released")

    // Still there, and nobody was told otherwise. Announcing a departure here
    // would take Bob out of everybody's buddy list while he is still reachable.
    Assert.Equal<string[]>([| "bob" |], running.Server.Presence.Everyone |> Array.map _.Handle.Value)
    Assert.Empty departures

    (desktop :> IDisposable).Dispose()
    Assert.True(waitFor 5000 (fun () -> departures.Count = 1), "the last device leaving was not a departure")

// ---------------------------------------------------------------------------
// Messages: the channel whose post may not be dropped
//
// Every test below exists because §13 withdrew an argument. The mailbox was
// built on Yjs updates being idempotent, order-independent and safe to discard,
// and none of that survives contact with an instant message. These are the
// guards on what replaced it.
// ---------------------------------------------------------------------------

/// Signs in and publishes a card, which is the pair of things that make somebody
/// reachable by message. Publishing is not optional: a message is sealed to a
/// key its recipient published, so a client that never published one cannot be
/// written to at all.
/// WAITS FOR THE CARD TO LAND, and not merely for it to be sent. Publishing is
/// a frame the server processes on its own schedule, so a test that asked for
/// somebody's card immediately after they arrived could be told, truthfully,
/// that there is no such card yet. Doing the wait here rather than at each call
/// site means no test can forget it.
let private arrive (running: Running) (who: Identity) =
    let client = running.Connect who
    client.SignIn()
    client.PublishCard()

    if not (waitFor 5000 (fun () -> (Accounts.cardFor running.Db who.Handle).IsSome)) then
        failwith $"{who.Handle.Value} signed in but the card never arrived"

    client

let private cardOf (client: Client) (who: Handle) =
    match client.AskFor(who, 5000) with
    | Card card -> card
    | other -> failwith $"expected a card, got {other.GetType().Name}"

[<Fact>]
let ``a card is served to whoever asks, and is signed by the identity it names`` () =
    use running = new Running()
    use aliceId = identity "alice"
    use bobId = identity "bob"

    do
        use bob = arrive running bobId
        Assert.True(waitFor 5000 (fun () -> (Accounts.cardFor running.Db bobId.Handle).IsSome))

    use alice = arrive running aliceId
    let card = cardOf alice bobId.Handle

    Assert.Equal(bobId.Handle, card.Handle)
    Assert.Equal<byte[]>(bobId.MessagingPublicKey, card.Messaging)

    // The property the directory rests on: the relay could have invented this
    // card, and the signature is what says it did not. Checked here the way a
    // client checks it, against the identity key rather than against anything
    // the relay said about it.
    Assert.True(Messaging.verifyCard card, "the served card was not signed by the identity it names")

[<Fact>]
let ``a card published for somebody else's handle is refused`` () =
    // The attack a key directory has to survive. If this were accepted, anybody
    // with an account could replace anybody's messaging key with one they hold
    // the private half of, and every message to that person would open for them.
    use running = new Running()
    use aliceId = identity "alice"
    use bobId = identity "bob"
    let refusals = ResizeArray<string>()
    running.Server.Refused.Add refusals.Add

    do
        use bob = arrive running bobId
        Assert.True(waitFor 5000 (fun () -> (Accounts.cardFor running.Db bobId.Handle).IsSome))

    use alice = running.Connect aliceId
    alice.SignIn()

    // Alice publishes a perfectly well-formed card. It is Bob's handle and
    // Alice's keys, and Alice's own signature over her own messaging key, so
    // nothing about it fails verification — it fails because the connection
    // publishing it proved it was alice.
    let stolen =
        { Messaging.cardOf aliceId with Handle = bobId.Handle }

    alice.Send(Card stolen)

    Assert.True(waitFor 5000 (fun () -> refusals.Count > 0), "a card for somebody else's handle was accepted")
    Assert.Contains("not its own handle", refusals[0])

    // And the real one is untouched.
    let served = (Accounts.cardFor running.Db bobId.Handle).Value
    Assert.Equal<byte[]>(bobId.MessagingPublicKey, served.Messaging)

[<Fact>]
let ``a message for a connected peer is stored as well as handed over`` () =
    // §13.2, and the reason is not caution. A message forwarded straight through
    // exists in exactly one place — a socket buffer — so a recipient whose
    // connection dies mid-write loses it with nothing anywhere able to notice.
    // Storing first means every message has a row, therefore an id, therefore
    // something to acknowledge.
    use running = new Running()
    use aliceId = identity "alice"
    use bobId = identity "bob"

    use bob = arrive running bobId
    use alice = arrive running aliceId
    Assert.True(waitFor 5000 (fun () -> running.Server.Presence.Everyone.Length = 2))

    let card = cardOf alice bobId.Handle
    alice.MessageTo(bobId.Handle, card.Messaging, "straight over and written down") |> ignore

    let _, post, _, body = bob.NextMessage(aliceId.MessagingPublicKey, 5000)
    Assert.Equal("straight over and written down", body)

    // Delivered AND still held, which is exactly what a note would not be.
    Assert.True(post > 0L, "a delivered message carried no post id to acknowledge")
    Assert.Equal(1, Mailbox.countOn running.Db MessageTraffic bobId.Handle)

[<Fact>]
let ``a message is kept until it is acknowledged, and then forgotten`` () =
    use running = new Running()
    use aliceId = identity "alice"
    use bobId = identity "bob"

    use bob = arrive running bobId
    use alice = arrive running aliceId
    Assert.True(waitFor 5000 (fun () -> running.Server.Presence.Everyone.Length = 2))

    let card = cardOf alice bobId.Handle
    alice.MessageTo(bobId.Handle, card.Messaging, "acknowledge me") |> ignore

    let _, post, _, _ = bob.NextMessage(aliceId.MessagingPublicKey, 5000)
    Assert.Equal(1, Mailbox.countOn running.Db MessageTraffic bobId.Handle)

    bob.Acknowledge [| post |]
    Assert.True(waitFor 5000 (fun () -> Mailbox.countOn running.Db MessageTraffic bobId.Handle = 0), "an acknowledged message was kept")

[<Fact>]
let ``an unacknowledged message is delivered again on the next sign-in`` () =
    // The durability claim, end to end. Bob receives a message and dies before
    // acknowledging it — which is what a client that crashes between the socket
    // and its own disk looks like from here — and the message is still there.
    use running = new Running()
    use aliceId = identity "alice"
    use bobId = identity "bob"

    use alice = arrive running aliceId

    do
        use bob = arrive running bobId
        Assert.True(waitFor 5000 (fun () -> running.Server.Presence.Everyone.Length = 2))
        let card = cardOf alice bobId.Handle
        alice.MessageTo(bobId.Handle, card.Messaging, "say it twice") |> ignore
        bob.NextMessage(aliceId.MessagingPublicKey, 5000) |> ignore
        // Leaves WITHOUT acknowledging.

    Assert.Equal(1, Mailbox.countOn running.Db MessageTraffic bobId.Handle)

    use returned = arrive running bobId
    let _, post, _, body = returned.NextMessage(aliceId.MessagingPublicKey, 5000)
    Assert.Equal("say it twice", body)

    returned.Acknowledge [| post |]
    Assert.True(waitFor 5000 (fun () -> Mailbox.countOn running.Db MessageTraffic bobId.Handle = 0), "the redelivered message was never cleared")

[<Fact>]
let ``a full message queue refuses new post and tells the sender`` () =
    // The other half of §13. The note channel trims the oldest and is right to;
    // doing that here would destroy somebody's message, so the sender is
    // refused instead — and told, because a message that silently did not arrive
    // is the failure this whole channel was rebuilt to stop having.
    use running = new Running(queueLimit = 2)
    use aliceId = identity "alice"
    use bobId = identity "bob"

    do
        use bob = arrive running bobId
        Assert.True(waitFor 5000 (fun () -> (Accounts.cardFor running.Db bobId.Handle).IsSome))

    use alice = arrive running aliceId

    Assert.True(
        waitFor 5000 (fun () ->
            running.Server.Presence.Everyone
            |> Array.forall (fun peer -> peer.Handle.Folded <> bobId.Handle.Folded)),
        "the server still believed bob was connected"
    )

    let card = cardOf alice bobId.Handle

    for i in 1..2 do
        alice.MessageTo(bobId.Handle, card.Messaging, $"message {i}") |> ignore

    Assert.True(waitFor 5000 (fun () -> Mailbox.countOn running.Db MessageTraffic bobId.Handle = 2), "the queue did not fill")

    alice.MessageTo(bobId.Handle, card.Messaging, "one too many") |> ignore

    let who, why = alice.NextUndeliverable 5000
    Assert.Equal(bobId.Handle, who)
    Assert.Contains("cannot take more", why)

    // NOTHING WAS EVICTED, which is the point. A trim here would have thrown
    // away "message 1" to make room, and neither party would ever have known.
    Assert.Equal(2, Mailbox.countOn running.Db MessageTraffic bobId.Handle)

    let kept =
        Mailbox.peek running.Db bobId.Handle
        |> List.map (fun post -> Messaging.tryOpen bobId aliceId.MessagingPublicKey post.Payload)
        |> List.map (fun opened ->
            match opened |> Option.map Codec.decode with
            | Some(Message(_, _, body)) -> body
            | _ -> "unopenable")

    Assert.Equal<string list>([ "message 1"; "message 2" ], kept)

[<Fact>]
let ``ageing out never touches a message`` () =
    // A guard against reintroducing the defect §13 removed, by a route that
    // would look innocent: the prune exists for note post nobody came back for,
    // and a WHERE clause that forgot the channel would quietly delete
    // unacknowledged messages on every sign-in.
    use running = new Running()
    use aliceId = identity "alice"
    use bobId = identity "bob"

    do
        use bob = arrive running bobId
        Assert.True(waitFor 5000 (fun () -> (Accounts.cardFor running.Db bobId.Handle).IsSome))

    use alice = arrive running aliceId

    Assert.True(
        waitFor 5000 (fun () ->
            running.Server.Presence.Everyone
            |> Array.forall (fun peer -> peer.Handle.Folded <> bobId.Handle.Folded)),
        "the server still believed bob was connected"
    )

    let card = cardOf alice bobId.Handle
    alice.MessageTo(bobId.Handle, card.Messaging, "older than the cutoff") |> ignore
    alice.PostTo(bobId.Handle, joinKey, note "a note of the same age")

    Assert.True(waitFor 5000 (fun () -> Mailbox.count running.Db bobId.Handle = 2), "the post never arrived")

    // Everything in the queue is now "old". The note goes; the message stays.
    Mailbox.prune running.Db -1.0 |> ignore

    Assert.Equal(0, Mailbox.countOn running.Db NoteTraffic bobId.Handle)
    Assert.Equal(1, Mailbox.countOn running.Db MessageTraffic bobId.Handle)

[<Fact>]
let ``a client cannot acknowledge away somebody else's post`` () =
    // Post ids are row numbers, so they are guessable. An unscoped delete would
    // let anybody with an account destroy anybody else's waiting mail.
    use running = new Running()
    use aliceId = identity "alice"
    use bobId = identity "bob"
    use malloryId = identity "mallory"

    do
        use bob = arrive running bobId
        Assert.True(waitFor 5000 (fun () -> (Accounts.cardFor running.Db bobId.Handle).IsSome))

    use alice = arrive running aliceId

    Assert.True(
        waitFor 5000 (fun () ->
            running.Server.Presence.Everyone
            |> Array.forall (fun peer -> peer.Handle.Folded <> bobId.Handle.Folded)),
        "the server still believed bob was connected"
    )

    let card = cardOf alice bobId.Handle
    alice.MessageTo(bobId.Handle, card.Messaging, "for bob's eyes") |> ignore
    Assert.True(waitFor 5000 (fun () -> Mailbox.countOn running.Db MessageTraffic bobId.Handle = 1))

    let held = Mailbox.peek running.Db bobId.Handle |> List.map _.Id |> List.toArray

    use mallory = arrive running malloryId
    mallory.Acknowledge held

    // Given a moment to do the damage it is not allowed to do.
    Thread.Sleep 300
    Assert.Equal(1, Mailbox.countOn running.Db MessageTraffic bobId.Handle)
