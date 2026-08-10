namespace EmuSen.Chariot

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
/// Keyed by the folded handle rather than by connection, so a second connection
/// under the same handle replaces the first rather than producing one person
/// twice in everybody's buddy list. That also means signing in from a second
/// machine displaces the first, which is a real limitation and is stated in
/// docs/Chariot_Design.md §10 rather than discovered.
type Presence() =
    let connected = ConcurrentDictionary<string, PeerInfo * Wire>()

    /// Adds or replaces, and returns the connection it displaced if any, so the
    /// caller can close it rather than leave a socket nobody is reading.
    member _.Arrive(peer: PeerInfo, wire: Wire) =
        let mutable displaced = Unchecked.defaultof<PeerInfo * Wire>
        let existed = connected.TryGetValue(peer.Handle.Folded, &displaced)
        connected[peer.Handle.Folded] <- (peer, wire)
        if existed then Some(snd displaced) else None

    /// Removes only if this exact sender is still the registered one. Without
    /// that check, a displaced connection closing afterwards would evict the
    /// connection that replaced it.
    member _.Depart(peer: PeerInfo, wire: Wire) =
        match connected.TryGetValue peer.Handle.Folded with
        | true, (_, current) when System.Object.ReferenceEquals(current, wire) ->
            connected.TryRemove peer.Handle.Folded |> ignore
            true
        | _ -> false

    member _.Everyone = connected.Values |> Seq.map fst |> Seq.toArray

    /// The roster as one person sees it: everybody except themselves. A buddy
    /// list containing you is a buddy list with a bug in it.
    member this.RosterFor(peer: PeerInfo) =
        this.Everyone
        |> Array.filter (fun other -> other.Handle <> peer.Handle)
        |> Array.sortBy (fun other -> other.Handle.Folded)

    /// Republished to everybody whenever the set changes, which is the model
    /// the forked C# server's ChatHub had right and is the reason that fork was
    /// worth taking at all - docs/Chariot_Design.md §2.
    member this.Broadcast() =
        for peer, wire in connected.Values do
            wire.Say(Roster(this.RosterFor peer))

    /// The wire for a handle, if that handle is connected right now. This is
    /// the whole of routing's lookup: present means hand it over, absent means
    /// put it in the mailbox.
    member _.WireFor(handle: Handle) =
        match connected.TryGetValue handle.Folded with
        | true, (_, wire) -> Some wire
        | _ -> None
