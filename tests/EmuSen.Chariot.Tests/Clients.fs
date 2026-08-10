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
    let key = Crypto.deriveKey passphrase
    let client = new TcpClient()
    do client.Connect(host, port)
    do client.NoDelay <- true
    let stream = client.GetStream()
    do Handshake.asJoiner stream key ct |> _.GetAwaiter().GetResult()

    let read () =
        Framing.readFrame stream key ct |> _.GetAwaiter().GetResult()

    let write frame =
        Framing.writeFrame stream key Direct frame ct |> _.GetAwaiter().GetResult()

    member _.Identity = identity
    member _.Send(frame) = write frame

    /// The exchange as documented: the server challenges, we say who we are and
    /// sign what it sent.
    member _.SignIn() =
        let rec awaitChallenge () =
            match read () with
            | _, Challenge nonce -> nonce
            | _ -> awaitChallenge ()

        let nonce = awaitChallenge ()
        write (Hello(identity.Peer, identity.PublicKey, Version.Protocol))
        write (Proof(Attestation.prove identity nonce))

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
