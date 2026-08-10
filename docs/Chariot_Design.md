# Chariot — design record

## 1. What Chariot is

Chariot is the always-on half of Pegasus: the thing that knows who is online,
lets one person reach another by handle instead of by address and port, and
holds what it cannot deliver until the other side comes back.

It is deliberately **not** a peer. It never holds a note, never merges an
update, and cannot read a word either party writes. §4 explains why that
sentence is a correction to something `Pegasus_Sync.md` already claims.

Two people, one notepad. Everything here is sized for that.

## 2. What it inherited

The repository is a hard fork of `cheshire-84/8BB-MSGR.NET`, offered to this
project after being set aside. It arrived as 231 lines of hand-written C# in two
parts, and it is worth being exact about which part is which.

**The TODO REST API** — `Program.cs`, `Models/TodoItem.cs`,
`Data/TodoDbContext.cs`, one EF Core migration, and a SQLite file. This is the
scaffold the repository grew from, still named `8BB-TODO-.NET`. It has nothing
to do with messaging and nothing here builds on it.

**`Hubs/ChatHub.cs`** — 73 lines, and the reason this fork was worth taking. It
keeps a `ConcurrentDictionary` from SignalR connection id to username, announces
arrivals and departures, and pushes a sorted user list to every client on every
change.

That is the presence model, and it is the right one. A live registry of who is
connected, republished to everyone whenever it changes, is exactly what Pegasus
lacks and exactly what makes a buddy list feel like a buddy list. Getting to
that idea is the hard part; the code expressing it is not.

### 2.1 Why none of the code survives

Every detail of the hub is built for a different application, and the mismatch
is not the kind that can be patched:

- **Everything is `Clients.All`.** Pegasus is pairwise. There is no notion of
  routing to one recipient anywhere in the hub.
- **There is no authentication.** `JoinChat(username)` believes whatever it is
  told. Pegasus already has this hole (`Pegasus_Identity.md` §2) and a server is
  the place it stops being tolerable.
- **The payload is chat.** `ChatMessage` carries `Sender`, `Content`,
  `CodeSnippet`. Pegasus moves sealed binary frames whose contents the server is
  not entitled to see.
- **Nothing persists.** `ChatMessage` is not in the `DbContext`; only
  `TodoItems` is. A restart forgets everything.
- **There are no tests**, and no test project.

So: the concept is kept and credited, the code is not. This is recorded plainly
rather than quietly, because "we forked a repository" and "we kept fifty lines
of an idea from it" are different claims and only the second one is true.

### 2.2 Housekeeping the fork arrived with

No `.gitignore`, so 200 of 211 tracked files are `bin/` and `obj/` — 66 MB of
build output — alongside a committed `todo.db`. EF Core also pulls in
`SQLitePCLRaw.lib.e_sqlite3` 2.1.11, which carries a known high-severity
advisory. All three problems are removed by §3 rather than fixed.

## 3. Why there is no web stack here at all

Chariot speaks the Pegasus frame protocol over TCP (`Pegasus_Sync.md` §3). That
single decision deletes ASP.NET Core, SignalR, Entity Framework, the SQLite
provider and its advisory, OpenAPI, and the REST surface — the entire dependency
footprint the fork arrived with.

The alternative was to keep SignalR and send `byte[]` payloads through it. It
was rejected for now, and the reasons are worth keeping because they may change:

- Pegasus already has a tested framing, sealing and session layer. A second wire
  format means a second implementation of all three, and a SignalR client living
  inside the desktop application beside the socket client it already has.
- `Pegasus_Sync.md` §1 already promised that adding a relay "should not require
  the protocol to learn a new role". Reusing the frames is what makes that
  promise cheap to keep.

What SignalR would have bought, and what is therefore deferred with it:
WebSocket transport is far friendlier to NAT and to corporate proxies than a raw
TCP port, and it keeps a browser client possible — which is on the deferred list
in the Pegasus README. If either of those becomes the priority, this section is
where to start arguing, and the frame layout is deliberately transport-agnostic
so the argument stays about transport.

## 4. A correction to `Pegasus_Sync.md` §1

That section says:

> An always-on relay is deferred, not rejected. The session abstraction is shaped
> so that a relay is simply a peer that never disconnects; adding one should not
> require the protocol to learn a new role.

**That is only true of a relay allowed to read your notes.** Merging a Yjs
update requires the plaintext of that update. A relay that holds a replica must
therefore be able to decrypt everything both parties write, and the end-to-end
property Pegasus has today would be gone.

Keeping the sealing means Chariot cannot merge, which means it is not a peer. It
is a router and a mailbox. The protocol does have to learn something new: a way
to say *who a sealed payload is for*, since the current framing seals the whole
frame and leaves nothing for an intermediary to read (§5).

`Pegasus_Sync.md` §1 is to be rewritten in the pass that lands the envelope, not
left standing. The prediction it made was reasonable and it was wrong.

## 5. Routing without reading

An intermediary needs a destination it can read, and today's frame is sealed end
to end with nothing outside the seal. So a relayed frame gains a plaintext
envelope:

    [ int32 length ][ envelope: destination, in the clear ][ sealed frame ]

The sealed part is unchanged and unreadable to Chariot. Only the envelope is
new, and it carries the least that routing can work with.

**This leaks metadata, and that is unavoidable.** Chariot necessarily learns who
is connected, who sends to whom, when, and how many bytes. It does not learn
content. Anyone who needs the routing itself hidden wants an onion router, not
this, and should be told so rather than reassured.

A channel identifier derived from the join code was considered instead of a
handle, so that Chariot would route without learning who talks to whom. It is
not obviously worth it: presence already requires Chariot to know handles and
connections, so the handle is known anyway, and the second identifier is
machinery that buys very little. Recorded so it is not re-proposed without a
better argument.

## 6. The mailbox, and why the CRDT makes it trivial

When a recipient is offline, Chariot keeps the sealed payloads and delivers them
on reconnect.

`Pegasus_Sync.md` §4 establishes that the payloads are Yjs updates and that the
exchange is therefore idempotent and order-independent: a duplicated or late
update merges to the same document. Every hard part of a message queue —
ordering, exactly-once delivery, acknowledgements, deduplication — is
consequently **not needed**. Chariot can store opaque blobs, hand over
everything undelivered, and be correct. This is the single largest simplification
in the design and it comes entirely from a decision made long before Chariot
existed.

### 6.1 The mailbox is liveness, not durability

A cap on the mailbox looks alarming against a project whose founding constraint
is that no party may lose information. It is not, and the reason should be
stated so nobody is tempted to build an unbounded queue in the name of safety:

**every peer keeps a complete replica on its own disk** (README, and
`Pegasus_Format.md`). A sender that hands work to Chariot has not given it away
— it still holds it. If Chariot drops a queued blob, the two replicas converge
later, the first time both peers are online together. What is lost is
promptness, not content. Losing content would take the sender's disk failing as
well, which is a different problem and not one a queue fixes.

So the mailbox may be bounded by size and by age, and overflow is a
*degradation* — back to needing a live session — rather than a data-loss event.
The cap and the policy are still to be chosen; the argument that a cap is
permissible is settled here.

## 7. Authentication, and the pass it shares with Pegasus

Chariot must know that the connection claiming `RedQuE3n` holds `RedQuE3n`'s
key, or the buddy list is decoration and the mailbox will hand somebody else's
sealed post to whoever asks.

The keypair for this already exists. `Pegasus_Identity.md` §2 records that
handles are asserted rather than proven and names the fix: a signed challenge
against a pinned public key. That work and this work are the same work, and it
should be done once, in the shared core, rather than twice.

First registration of a handle at Chariot is trust-on-first-use: the first key
to claim a handle owns it, and a later mismatch is refused and reported. Nothing
better is available without an authority nobody wants to run.

## 8. The shared core, and `Pegasus_Design.md` §7

§7 collapsed Pegasus to one assembly, on the rule that a project boundary has to
buy somebody separability they are actually using. Chariot is that somebody, so
the rule is now satisfied rather than violated, and §7 is to be rewritten with
the new fact rather than treated as an obstacle.

What the core has to contain is small, because Chariot does very little:

    Types      PeerId, Handle, PeerInfo, Frame, the envelope
    Codec      frame and envelope encoding
    Crypto     the sealed envelope, the KDF, challenge and response
    Identity   keys, signing, verification — but not the file store

Notably **not** in it: `Document` and therefore YDotNet, since Chariot never
merges; `Store` and `Workspace`, since it holds no notes; anything Avalonia.
`Identity.fs` currently mixes key handling with the on-disk identity format and
wants splitting along that line — Chariot needs to verify a signature, not to
read anybody's identity file.

Distribution follows the pattern already in use for `EmuSen.LunaP`: a package,
consumed through `local-packages/` until it is on GitHub Packages.

## 9. The passes

| Pass | Delivers | Proved by |
|---|---|---|
| 0 | This document. Fork assessed, decisions recorded, repository cleaned of build output | Nothing to prove yet |
| 1 | `EmuSen.Pegasus.Core` split out and packaged; Pegasus consumes it; `Design §7` rewritten | The existing 94 tests pass unchanged — a split that needs a test rewritten was not a split |
| 2 | Signed challenge and key pinning, in the core; `Identity_.md` §2 hazard closed | New guards, each watched failing first |
| 3 | The envelope: routing header outside the seal; `Sync §1` and §3 corrected | Round-trip and rejection tests; a relay must not be able to open a payload |
| 4 | Chariot skeleton in F#: TCP listener, sign-in, presence, buddy list push | Two clients over loopback see each other appear and disappear |
| 5 | The mailbox: store, deliver on reconnect, bounded | A peer that was offline for the whole exchange converges on reconnect |
| 6 | Pegasus connects through Chariot: connect by handle, no address, no port | The pairing ritual in the README shrinks to picking a name |

Each pass ends green and is committed on its own. Passes 1 and 2 are in the
Pegasus repository; 3 touches both; 4 and 5 are Chariot; 6 is Pegasus again.

## 10. What Chariot will not do

- **Not read notes.** If a future decision gives it that power, it is a rewrite
  of this document, not an enhancement.
- **No REST API and no web UI.** The fork's endpoints are not being ported.
- **Not an account authority.** It knows handles and keys well enough to route
  and to refuse impostors. It is not a login service for anything else.
- **Not groups, yet.** `Pegasus_Sync.md` §1 records that a host accepts exactly
  one joiner. Chariot could lift that limit and deliberately does not in these
  passes; one thing at a time.
