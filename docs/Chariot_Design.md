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
advisory. All three problems are removed by §3 rather than fixed, and the C#
itself was deleted in pass 4 once the F# replaced it — 13 source files, none of
which anything here builds on. It remains in this repository's history, which is
the right place for code that was read and learned from rather than kept.

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

`Pegasus_Sync.md` §1 **has been rewritten**, in the pass that landed the
envelope, rather than left standing. The prediction it made was reasonable and
it was wrong, and it now says so in place.

## 5. Routing without reading

An intermediary needs a destination it can read, and today's frame is sealed end
to end with nothing outside the seal. So a relayed frame gains a plaintext
envelope:

    [ int32 length ][ envelope: destination, in the clear ][ sealed frame ]

The sealed part is unchanged and unreadable to Chariot. Only the envelope is
new, and it carries the least that routing can work with. It shipped in pass 3;
`Pegasus_Sync.md` §3.1 is the layout as built, with two envelopes — `Direct` and
`ToHandle` — and the rule that decides what Chariot may open at all.

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
| 4 | **Done.** TCP listener, sign-in, presence, buddy list push, accounts | Two clients over loopback see each other appear and disappear |
| 5 | **Done.** The mailbox: store, deliver on reconnect, bounded | A peer that was offline for the whole exchange converges on reconnect |
| 6 | **Done.** The transport: Pegasus connects through Chariot by handle | Two peers converge through a relay with no address or port; the ritual has not shrunk until the window offers it |
| 6a | **Done.** The sign-in and buddy list in the Pegasus window | Pegasus' README pairing section was rewritten because it became wrong |
| 7 | Chariot proves itself, control traffic gets a session key, and one person may be in two places | A client refuses a server whose key changed; a passphrase holder cannot read a roster; a laptop and a desktop are both present |

Each pass ends green and is committed on its own. Passes 1 and 2 are in the
Pegasus repository; 3 touches both; 4 and 5 are Chariot; 6 and 6a are Pegasus
again.

### 9.4 What pass 6a found

The window was the pass, and building it turned up one defect in the protocol
that the transport tests could not have caught, because they never made the
mistake a person makes.

Two people click **Open note** a few seconds apart. Whoever clicks first speaks
into a client with no conversation to receive it, so its `Hello` and `Challenge`
are dropped; when the second one opens, the late end proves itself and sends
`SyncStep1`, and the early end — never having had its own challenge answered —
refuses that as document traffic from an unproven peer. Half a handshake, looking
from the outside exactly like this server eating frames.

The fix is in the shared core, not here: the first `Hello` a conversation
receives is answered by re-sending our own opening move, once.
`Pegasus_Sync.md` §4.2 carries it. Chariot is unchanged, which is the correct
outcome — a client-side handshake defect should not be repaired in a router.

Two smaller things worth recording:

- **A relay address is worth remembering and a passphrase is not.** Pegasus keeps
  a `servers` table so the address is typed once — the address being stable is
  most of what a relay is for. It deliberately has no passphrase column, and
  `Pegasus_Identity.md` §8 has the argument.
- **A conversation now outlives a note.** Switching notes used to dispose the
  document a live session was holding, which was recorded as a hazard because
  nothing drove it. A buddy list makes it ordinary, so opening a note now drops
  the connection first. `Pegasus_Sync.md` §4.

## 9.1 What pass 4 established, and what it left open

The sign-in exchange is deliberately the same one two peers use — `Hello`
carrying a public key, a challenge each way, a proof — so the thing standing
between a stranger and the roster is code the Pegasus suite already exercises
rather than a second implementation written for a server. The forked C# server's
presence model is kept exactly as §2 credited it: a live registry republished to
everyone whenever it changes.

The passphrase is the front door and nothing more. It is `Crypto.deriveKey` over
a server secret, the same mechanism as a join code and carrying the same
fixed-salt weakness, so it decides who may open a session with this server, not
who they are. It is read from `CHARIOT_PASSPHRASE` rather than a command-line
argument, because argv is visible to every process on the machine through `ps`.

Three limitations are stated here rather than left to be discovered:

- **Chariot does not prove itself to a client.** A client's only assurance that
  it reached the right server is possession of the passphrase, so anyone holding
  that can impersonate this server. Giving Chariot its own keypair is a small
  pass and is not this one.
- **A handle may be signed in from one place at a time.** The presence registry
  is keyed by handle, so a second connection displaces the first rather than
  showing one person twice in everybody's list. Displacing is the deliberate
  choice; a laptop and a desktop signed in together is not supported.
- **Control traffic is sealed under the server key, which Chariot can read.**
  That is the point — sign-in and rosters are addressed to Chariot. Note
  traffic is a different envelope and a different key, and §5 is what keeps the
  two apart.

The envelope earns itself immediately here: `Direct` means the frame is for
Chariot and it may open it; `ToHandle` means it is somebody's note traffic,
sealed under a join code Chariot does not have, and it is moved without ever
being decoded. Routing arrives in pass 5; the refusal to decode is already true.

Both new guards were watched failing. Announcing a client before it proves
itself reddens the test that a silent client stays off the roster. Letting a new
key take over a registered handle — `INSERT OR REPLACE` instead of
`INSERT OR IGNORE` — reddens both trust-on-first-use tests, including the one
that restarts the server to prove the accounts table outlives the process while
presence deliberately does not.

## 9.2 Pass 7: the three things pass 4 left open

Written down now, in the order they depend on each other, because two of them
are the same fix seen from different ends.

### Chariot has no identity

A client's only assurance that it reached the right server is possession of the
passphrase, so anybody holding it can stand up a server and be believed. The
exchange is already symmetric — Chariot answers a client's `Challenge` — but the
client has nothing to check the answer against.

The fix is the one Pegasus already has: give Chariot a keypair, send it in
`Hello`, and have the client pin it on first connection and refuse a change
after. That is `KnownPeers` pointed at a server instead of a peer, and it should
reuse it rather than grow a second implementation.

### The passphrase reads everything

Control traffic — sign-in, rosters — is sealed under a key derived from the
server passphrase, with the fixed salt `Pegasus_Sync.md` §5 already calls a
weakness. So the passphrase is not only an admission gate: anyone holding it can
read who is online, and a recording of a session stays readable to whoever
learns it later.

This is the same fix as the one above, one step on. Once both ends have
keypairs, the control channel can carry an ephemeral key agreement — .NET has
`ECDiffieHellman` on the curve already in use — and the passphrase goes back to
being what it was supposed to be, a doorbell. Note traffic is unaffected either
way: it is sealed under a join code Chariot never has.

### A handle is in one place at a time

Presence is keyed by the folded handle, so a second connection displaces the
first. A laptop and a desktop signed in together is not supported, and the
displacement is silent to the person it happens to.

The change is to key presence by connection and de-duplicate by handle when
building a roster, so one person appears once while being reachable at several
places.

**This one has a consequence for the mailbox, and pass 5 must not be built
without it in view.** With one connection per handle, "delivered" is
unambiguous. With several, a payload handed to a laptop is *not* delivered to
the desktop, and a queue that drops on first delivery would silently strand the
second device. Pass 5 therefore treats delivery as per-recipient-handle and
keeps queued post until every connection for that handle has taken it — or,
more simply, keeps it until an acknowledgement that pass 7 can extend. The
cheap way out is available and is worth naming: because Yjs updates are
idempotent (§6), delivering the same blob to a second device twice costs
nothing, so the queue may over-deliver without being wrong.

## 9.3 What pass 5 built

Routing and the mailbox, and the CRDT paid for both exactly as §6 predicted:
there is no ordering to enforce, no acknowledgement protocol, no deduplication
and no exactly-once anywhere in this. Post is stored, handed over, and deleted
by id so that anything arriving mid-drain is not lost.

`FromHandle` joined the envelope, because an `Update` is opaque bytes and says
nothing about who wrote it — a client with two correspondents could not
otherwise tell their traffic apart. It is the relay's word rather than proof,
which is why a client sending one is refused: that is a client claiming to be
this server about who sent something.

Two bounds exist and both are deliberate. The queue is capped per recipient and
drops **oldest** first, since a full queue that refuses new post would stay full
forever. And post addressed to a handle that has never signed in here is refused
rather than queued, or any client could fill the disk by writing to names it
invented.

Three sabotages, three reds. Storing the payload re-wrapped instead of as it
arrived reddens the test that reads the database directly and asserts the server
cannot open what it holds. Trimming the newest instead of the oldest reddens the
bound. Delivering without clearing reddens the round trip, on the assertion that
post does not survive its own delivery.

One workflow trap was found and is recorded in `NuGet.config` rather than here,
because that is where somebody will hit it: repacking the core at the same
version does not propagate, since NuGet caches by id and version, and the build
fails on code you just wrote as though it did not exist.

## 10. What Chariot will not do

- **Not read notes.** If a future decision gives it that power, it is a rewrite
  of this document, not an enhancement.
- **No REST API and no web UI.** The fork's endpoints are not being ported.
- **Not an account authority.** It knows handles and keys well enough to route
  and to refuse impostors. It is not a login service for anything else.
- **Not groups, yet.** `Pegasus_Sync.md` §1 records that a host accepts exactly
  one joiner. Chariot could lift that limit and deliberately does not in these
  passes; one thing at a time.
