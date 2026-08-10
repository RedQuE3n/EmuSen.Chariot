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
    impostor.SignIn()

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
    impostor.SignIn()

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
let ``the queue is bounded, and drops the oldest`` () =
    // A bound is safe because the queue is liveness and not durability: the
    // sender still holds a complete replica, so what is lost is promptness.
    use running = new Running(queueLimit = 3)
    use aliceId = identity "alice"
    use bobId = identity "bob"

    do
        use bob = running.Connect bobId
        bob.SignIn()
        Assert.True(waitFor 5000 (fun () -> Accounts.all(running.Db).Length = 1))

    use alice = running.Connect aliceId
    alice.SignIn()

    for i in 1..6 do
        alice.PostTo(bobId.Handle, joinKey, note $"note {i}")

    Assert.True(waitFor 5000 (fun () -> Mailbox.count running.Db bobId.Handle = 3), "the queue was not bounded")

    let kept =
        Mailbox.peek running.Db bobId.Handle
        |> List.map (fun post -> Codec.decode (Crypto.openSealed joinKey post.Payload))

    Assert.Equal<Frame list>([ note "note 4"; note "note 5"; note "note 6" ], kept)

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
    alice.Forge(FromHandle(Handle.Parse "bob"), joinKey, note "pretending to be bob")

    Assert.True(waitFor 5000 (fun () -> refusals.Count > 0), "a forged FromHandle was accepted")
    Assert.Contains("not a client's to write", refusals[0])
