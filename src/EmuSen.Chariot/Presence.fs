namespace EmuSen.Chariot

open System
open System.Collections.Concurrent
open EmuSen.Pegasus

/// The two ways Chariot puts bytes on a connection, and they are not the same
/// thing.
///
/// `Say` is Chariot speaking for itself — a roster, a goodbye — sealed under the
/// server key because it is addressed to this client. `Forward` is somebody
/// else's sealed payload going past, and Chariot supplies only the envelope; the
/// bytes are handed on exactly as they arrived, because it could not open them
/// if it wanted to.
type Wire =
    { Say: Frame -> unit
      Forward: Envelope -> byte[] -> unit }

/// Who is signed in right now.
///
/// In memory and nowhere else, on purpose. Presence is disposable by nature -
/// the same argument `Pegasus_Sync.md` §4, in the EmuSen.Pegasus repository makes for not persisting a caret. A
/// roster that survived a restart would be a list of people who are not there,
/// which is worse than no list, and the truth is one reconnect away anyway.
///
/// KEYED BY CONNECTION, NOT BY HANDLE, and that is the whole of one third of
/// pass 7.
///
/// It used to be keyed by the folded handle, which meant a second connection
/// under the same handle replaced the first: signing in from a laptop silently
/// knocked the desktop off, and the person it happened to was not told. One
/// person is now present at as many places as they are signed in from, and the
/// de-duplication happens where it belongs — when a roster is built, so a buddy
/// list shows one entry per person rather than one per device.
///
/// The consequence that matters is in routing: a payload handed to one of
/// somebody's connections is NOT delivered to the others, so `WiresFor` returns
/// all of them and the server hands to every one. Over-delivery costs nothing
/// here, because Yjs updates are idempotent — docs/Chariot_Design.md §6 — which
/// is the same property that let the mailbox skip acknowledgements entirely.
type Presence() =
    let connected = ConcurrentDictionary<Guid, PeerInfo * Wire>()

    /// Registers a connection and returns the token that identifies it.
    ///
    /// A token rather than the handle, because the handle is no longer unique
    /// and departing has to remove the connection that actually left. Nothing
    /// is displaced any more; there is nothing here to displace.
    member _.Arrive(peer: PeerInfo, wire: Wire) =
        let token = Guid.NewGuid()
        connected[token] <- (peer, wire)
        token

    /// Removes one connection. Returns whether that was this person's last one,
    /// so the caller only announces a departure when they have actually gone
    /// rather than merely closed a laptop lid.
    member _.Depart(token: Guid) =
        match connected.TryRemove token with
        | true, (peer, _) -> not (connected.Values |> Seq.exists (fun (other, _) -> other.Handle = peer.Handle))
        | _ -> false

    /// One entry per person, however many places they are signed in from. The
    /// first connection's PeerInfo wins, and they are all the same person's, so
    /// there is nothing to choose between them.
    member _.Everyone =
        connected.Values
        |> Seq.map fst
        |> Seq.distinctBy (fun peer -> peer.Handle.Folded)
        |> Seq.toArray

    member _.Connections = connected.Count

    /// The roster as one person sees it: everybody except themselves. A buddy
    /// list containing you is a buddy list with a bug in it, and a buddy list
    /// containing you twice is the bug this pass removed.
    member this.RosterFor(peer: PeerInfo) =
        this.Everyone
        |> Array.filter (fun other -> other.Handle <> peer.Handle)
        |> Array.sortBy (fun other -> other.Handle.Folded)

    /// Republished to every CONNECTION whenever the set changes — a person with
    /// two devices needs the new roster at both. The model itself is the one
    /// the forked C# server's ChatHub had right and is the reason that fork was
    /// worth taking at all - docs/Chariot_Design.md §2.
    member this.Broadcast() =
        for peer, wire in connected.Values do
            wire.Say(Roster(this.RosterFor peer))

    /// Every connection a handle is signed in on. Empty means absent, which is
    /// the whole of routing's lookup: present means hand it over — to all of
    /// them — and absent means put it in the mailbox.
    member _.WiresFor(handle: Handle) =
        connected.Values
        |> Seq.filter (fun (peer, _) -> peer.Handle = handle)
        |> Seq.map snd
        |> Seq.toArray
