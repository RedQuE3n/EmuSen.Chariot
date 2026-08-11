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

### 7.1 Chariot's own identity, and where its private key lives

The paragraphs above are about clients proving themselves to Chariot. Pass 7
closed the other direction, which was open for three passes and stated as a
limitation each time: **a client had no way to tell one server from another.**

Chariot answered a client's `Challenge` with an HMAC over the passphrase key.
That proves possession of the passphrase — which every client holds — so it
proved nothing about identity, and anybody with the passphrase could stand up a
server and be believed by everyone. Chariot now has a keypair of its own, sends
it in `Hello`, and signs the client's nonce with it. The client pins it exactly
the way it pins a person: `KnownPeers`, trust on first use, refuse a change. One
implementation, one table, and a server and a person share one namespace of
handles per owner, which is deliberate — one name, one key.

The handle is fixed on first run and stored beside the key. A database asked to
run under a different name is refused rather than renamed: taking the new name
would present the same key under a name nobody has pinned, and keeping the old
one would silently ignore what the operator asked for. Both are worse than
stopping, so `chariot` prints why and exits non-zero. It also prints the
fingerprint on startup, because that is the only thing an operator can read
aloud to somebody whose client is refusing to connect — a refusal means the
pinned key changed, and only a person can tell "we rebuilt the server" from
"that is not our server".

**The private key is stored unsealed, mode 0600, and this is the decision here
most worth reading before changing.** Pegasus seals a user's private key under a
password and a test asserts it never reaches disk in the clear
(`Pegasus_Identity.md` §3). That works because a person is present to type the
password. A server has nobody: it starts at boot, unattended. Sealing it under
`CHARIOT_PASSPHRASE` would put the key and the thing that opens it in the same
environment and call it protection — and worse, that passphrase is shared with
every client, so the server's key would be sealed under a secret its own users
hold.

So it is stored the way an SSH host key is stored, for the reason SSH stores it
that way, and the honest statement is that **anyone who can read this database is
this server.** Nothing here pretends to defend against an attacker who already
has the file. The difference from Pegasus' rule is not an inconsistency; it is
the same reasoning applied to a machine with no user at the keyboard.

### 7.2 The passphrase is a doorbell again

Control traffic — sign-in, rosters — was sealed under a key derived from the
passphrase, with the fixed salt `Pegasus_Sync.md` §5 calls a weakness. Every
client holds that passphrase, so every client could read every other client's
roster off the wire, and a recorded session stayed readable to whoever later
learned one shared secret.

Once both ends have keypairs the fix is one step on: after each has proved
itself, each sends an ephemeral P-256 key signed with the identity it just
proved, and both derive a session key from the pair. `Pegasus_Sync.md` §4.3 has
the exchange, the ordering, and what the signature on an ephemeral is for. The
short version: an unsigned ephemeral can be replaced by whoever carries it, which
is unauthenticated Diffie-Hellman and no better than the passphrase it replaces.

Note traffic is unaffected, and that is the point of keeping the two envelopes
apart. It is sealed under a join code Chariot never has; wrapping it in a second
key agreed *with* this server would be strictly worse than leaving it alone.

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
| 7 | **Done.** Chariot proves itself, control traffic gets a session key, and one person may be in two places | A client refuses a server whose key changed; a passphrase holder cannot read a roster; a laptop and a desktop are both present |

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

Three limitations were stated here rather than left to be discovered. **All
three were closed by pass 7**, and they are kept below with what became of them,
because a limitation that is quietly deleted once it is fixed leaves a reader
unable to tell which version of the design they are looking at.

- **Chariot does not prove itself to a client.** ~~A client's only assurance that
  it reached the right server is possession of the passphrase, so anyone holding
  that can impersonate this server.~~ Closed: §7.1. Chariot has a keypair, sends
  it in `Hello`, and signs the client's nonce; the client pins it.
- **A handle may be signed in from one place at a time.** ~~The presence registry
  is keyed by handle, so a second connection displaces the first.~~ Closed: §9.2.
  Presence is keyed by connection and de-duplicated when a roster is built.
- **Control traffic is sealed under the server key, which Chariot can read.**
  Still true of what is addressed to Chariot, and that remains the point — it has
  to read a sign-in. What changed is *which* key: it is agreed per connection
  rather than derived from the passphrase, so Chariot reading it no longer means
  every other client can too. §7.2.

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

**Built.** What follows is the plan as it was written, kept because the reasoning
in it is what the code does; §9.5 records where it turned out to be wrong.

Written down in the order they depend on each other, because two of them are the
same fix seen from different ends.

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

## 9.5 What pass 7 built, and the one place the plan was wrong

All three landed. The exchange is `Pegasus_Sync.md` §4.3; the server's identity
and where its private key lives is §7.1 above; presence keyed by connection is
`Presence.fs`, and the roster de-duplicates by handle so one person appears once
while being reachable at several places.

**The plan was wrong about the mailbox, and in the safe direction.** §9.2 warned
that with several connections per handle a queue that drops on first delivery
would strand the second device, and proposed keeping post until every connection
had taken it, or an acknowledgement. Neither was needed. Routing hands a payload
to *every* connection a handle has, so nothing is queued while any device is
present, and the queue is untouched by the change — it is still "nobody is here,
hold this". What is genuinely not covered is a device that is offline while
another is on: it gets nothing from the queue, and converges through `SyncStep1`
on its next conversation instead. That is the mailbox being liveness rather than
durability (§6.1) rather than a gap, and it is the same answer that section
already gave.

Over-delivery is free for the reason §6 gives: Yjs updates are idempotent. The
cheap way out was available and it was the right one.

Seven sabotages, seven reds, four here and three in Pegasus. Minting a fresh
server key on every start reddens the restart test. Never switching off the door
key reddens six, because a client that cannot read anything after sign-in cannot
do anything. Displacing a second connection for a handle reddens both
multi-device tests. Treating any departure as a departure reddens the one that
says closing a laptop must not take somebody out of everybody's buddy list. In
Pegasus: skipping the signature on an ephemeral, keeping control traffic under
the passphrase, and believing whatever server turns up.

One test was found to have been passing on timing rather than on a guarantee. It
posted to a handle the moment that handle's socket was disposed, and a payload
for a connection the server has not yet noticed is gone gets handed to a dying
socket and dropped instead of queued. Its sibling already waited for the
departure; this one now does too. Recorded rather than quietly fixed, because a
test that passes for the wrong reason is worth knowing about.

## 10. What Chariot will not do

- **Not read notes.** If a future decision gives it that power, it is a rewrite
  of this document, not an enhancement.
- **No REST API and no web UI.** The fork's endpoints are not being ported.
- **Not an account authority.** It knows handles and keys well enough to route
  and to refuse impostors. It is not a login service for anything else.
- **Not groups, yet.** `Pegasus_Sync.md` §1 records that a host accepts exactly
  one joiner. Chariot could lift that limit and deliberately does not in these
  passes; one thing at a time.

---

## 11. The relicence, and the question a fork has to answer

Chariot is **MIT**.

### 11.1 The chain, and where it stopped

The licence arrived here at the end of a chain, and every link in it was a consequence rather than a decision. EmuSen chose GPL-3.0. LunaP was a folder in EmuSen and carried the term out with it when it left, without re-deciding it. Pegasus links LunaP, so its design record wrote that its licence was "not a free choice". Chariot links `EmuSen.Pegasus.Core`, so this README said the same thing in the same words: *"GPL-3.0-or-later, as a consequence of linking `EmuSen.Pegasus.Core`, which is published under it."*

Four projects, one decision, made once by the only one of them that is an emulator. LunaP re-decided first (`docs/LunaP.md` §25 there), then Pegasus (`Pegasus_Design.md` §14), and each found the same thing: nothing in its own dependency tree had been imposing a copyleft term either. Chariot is the last link, and EmuSen — the one project that actually chose GPL-3.0 on its own account — keeps it.

**The mechanical part of this section is `EmuSen.Pegasus.Core` 0.3.0.** Core 0.2.x is GPL-3.0-or-later on nuget.org. A program cannot honestly put an MIT file at its root while linking a GPL library, because the licence is a statement about the work as distributed and the distributed work would contain both. So the reference in `EmuSen.Chariot.fsproj` moving from 0.2.2 to 0.3.0 is not housekeeping alongside this change; it is the change. The protocol is untouched — same wire, frame for frame, Hello still says 4.

### 11.2 The fork, and whether MIT is ours to grant

Every other project in this family had one copyright holder and a boring answer. This one does not, and §2 is why: **the repository is a hard fork of `cheshire-84/8BB-MSGR.NET`**, and the first commit in its history — `63fc015`, "first commit" — is not ours. Twelve commits, eleven by one author and one by another.

Two facts decide it, and both were checked rather than remembered.

**Upstream carries no licence.** `cheshire-84/8BB-MSGR.NET` is a public repository with no `LICENSE` file, and the fork's first commit brought none either. That cuts both ways and the unfavourable way matters more: no licence means no grant, so the fork rests on the offer §2 describes — the repository "offered to this project after being set aside" — rather than on any written term. It also means **the GPL here was never inherited from upstream**. It was added by us, in `9feca8c`, "Ship the licence file the README admitted was missing". Chariot's GPL was our own act, taken for the reason in §11.1.

**None of the forked code survives, and this was verified rather than assumed.** §2.1 already argued it at length — everything in `ChatHub.cs` is `Clients.All` where Pegasus is pairwise, the payload is chat where Pegasus moves sealed frames, nothing persists, there are no tests. What that section asserts, this one measured: there is **not one `.cs` file left in the repository**. `Program.cs`, `Hubs/ChatHub.cs`, `Models/ChatMessage.cs` and `Data/TodoDbContext.cs` are all gone, deleted in pass 4 once the F# replaced them. §2.1's sentence holds exactly as written — *"the concept is kept and credited, the code is not"* — and a concept is not a thing copyright reaches.

So the work this repository distributes is entirely ours, and MIT over it is ours to grant.

**What that argument does not cover, stated plainly rather than left for a reader to notice.** `63fc015` is still in the history, and `git log` will hand anybody 231 lines of somebody else's C# under no licence at all. The MIT file at the root governs the work, not every blob ever committed beneath it, and that is the ordinary reading — but it is a reading, and this section would be dishonest if it presented the question as not having been asked.

**The alternative was to ask, and it was considered rather than overlooked.** A written line from Cheshire would close the question permanently and cost one message. It was not taken because the fork's contribution is already established, in writing and in detail, as an idea rather than an expression, and §2.1 was written before there was any licence question riding on it — which is the strongest form that record could take. Recorded here so that if the question is ever reopened, the reasoning is on the page and not reconstructed. **The credit in §2 stays where it is, and is not contingent on the licence.**

### 11.3 The audit

Read out of each package's own `<license>` expression rather than recalled:

| Licence | Packages |
|---|---|
| MIT | `EmuSen.Pegasus.Core` (0.3.0), `Microsoft.Data.Sqlite`, `Microsoft.NET.Test.Sdk` |
| Apache-2.0 | `SQLitePCLRaw.bundle_e_sqlite3`, `xunit`, `xunit.runner.visualstudio` |

No copyleft in the tree. The Apache-2.0 entries are permissive and carry notice terms rather than reciprocal ones, and two of the three are test-only. The `SQLitePCLRaw` pin is the one that ships, and §2.2 already records why it is pinned at 3.0.5 — the advisory, not the licence.

### 11.4 What stays GPL

`v0.1.0` was released as compiled binaries under GPL-3.0-or-later and **stays that way**. A grant already made to somebody who took the work is not withdrawn by a later and looser one, and source for those binaries remains this repository at that tag. A relicence is not a recall.

Chariot is not itself a published package, so unlike LunaP and Core there is no nuget.org metadata frozen behind it — the only artefact carrying the old term is that release, and it keeps it honestly.

## 12. The release became a workflow, and the relay can prove more than the notepad

§11 relicensed this repository, which made a second binary release necessary, and going to cut one exposed what the first had been: four `dotnet publish` runs on one Linux laptop, staging directories assembled beside them by hand, and `gh release create` typed at the end. **No file in this repository produced those binaries.** Worse here than next door, because until now `.github/workflows/` did not exist at all — there was no CI in this repository, so `dotnet test` ran when somebody remembered to run it and never otherwise.

`release.yml` fixes both at once. It fires on a `v*` tag, runs the suite before anything is built, builds each RID on the operating system it targets, and creates the release from artefacts it checksums itself. The version comes from the tag and is passed as `-p:Version`, so the tag decides what the binary reports; the `<Version>` in `EmuSen.Chariot.fsproj` is the local default and is kept in step, because a version written in two places is one that will eventually disagree with itself.

The archive layout is v0.1.0's, and it was **read off the published artefact rather than remembered** — `Chariot-<rid>/` holding the executable, `LICENSE` and `README.md`. There is no `.app` bundle on macOS and that asymmetry with Pegasus is deliberate: a daemon has no window, so a bundle would buy it nothing but a Finder icon it never shows.

### 12.1 The smoke test, and why this repository gets one when Pegasus does not

This is the part worth reading, and it is the one place where being the boring program in the family is an advantage.

Pegasus is a GUI. A CI runner has no display and the application has no `--help`, so its release can build binaries on the right operating system and still not start one — its notes say so, and `Pegasus_Design.md` §15.1 states the limit rather than dressing it up. **Chariot is a daemon**, which means it can be started, asked a question, and made to answer on every platform it ships to. So it is.

Three checks run against the *staged* binary — the exact bytes that go into the archive — with the database and log written outside the staging directory so nothing extra lands in the download. In increasing order of what they prove:

1. **`--help` prints usage and exits 2.** Proves the single-file bundle self-extracts and the runtime comes up at all. The exit code is asserted rather than tolerated: `--help` goes through the same `Error` path as a bad option in `Program.fs`, which is a real decision, and a future edit moving it to 0 should turn this red rather than pass quietly.
2. **It refuses to start with no `CHARIOT_PASSPHRASE`.** Proves the refusal that stops a relay coming up open to the world still fires in a *released* build, not merely in a test binary.
3. **It starts, opens its accounts database, and reaches the listening line.** This is the one worth having. `--help` never touches SQLite, so only this proves `e_sqlite3` was carried into the bundle by `IncludeNativeLibrariesForSelfExtract` and extracted at runtime. **A missing native is invisible until a database is opened**, which on a relay is the first thing that happens after somebody deploys it — and the first person to open one must not be a user.

The third check is the reason the flag in the publish line is not housekeeping. Without it the binary builds, packages, uploads and dies on first run, and every step before this one would have been green.

Rehearsed locally before being committed, on `linux-x64`: all three pass, the server prints `chariot listening on 47420, accounts in smoketmp/smoke.db`, and a 28,672-byte database appears. Port 47420 rather than the default 7420 so a runner with something else bound cannot make it flaky.

### 12.2 What it still does not cover

- **Nothing is signed or notarised**, so `SHA256SUMS` — generated on the runner over the artefacts about to be uploaded — is the only integrity evidence a download has.
- **The smoke test starts the server; it does not connect to it.** No client signs in, no frame crosses the wire, no note is relayed. What is proven is that the binary runs and its storage works, not that the protocol does; the suite is what covers the protocol, and it runs on `ubuntu-latest` only.
- **No `linux-arm64` build**, so a Raspberry Pi still cannot run a released relay. Building from source on the machine is one `dotnet publish`.
- **The macOS and Windows binaries are now built and started on their own operating systems**, which is a genuine change from v0.1.0 — but by a runner, not by a person, and nobody has run a relay in anger on either.

### 12.3 0.2.0 was spent on a retired runner image

**`v0.2.0` was tagged, built three platforms, published nothing, and did not fail.** Recorded rather than quietly retagged, because the failure mode is the part worth knowing.

**The tag itself no longer exists** — it was deleted once 0.3.0 had shipped, so the release list does not carry a version that published nothing. This section is therefore the only remaining record of it, and nothing was ever distributed under `v0.2.0`, so removing it took nothing back from anybody.

The matrix asked for `macos-13` for the `osx-x64` build, and **that image was retired on 4 December 2025**. The observed run — `test` green, `linux-x64` green, `win-x64` green, `osx-arm64` green, `osx-x64` **queued** indefinitely — is what a retired label looks like from the outside. The replacement, now named, is `macos-15-intel`.

**A retired label does not error, it queues.** No red job, no message, nothing in the summary saying the label is gone. `fail-fast: true` never tripped because nothing failed, and §12's rule that a release missing a platform is worse than no release worked exactly as designed: `release` needs all four builds, three arrived, and it never started. The safety property held and the diagnosis was still invisible.

**The pin caused it and unpinning is still wrong.** Naming images explicitly rather than `macos-latest` was argued for on the grounds that `latest` has already moved architecture once and would silently turn `osx-x64` into a cross-compile. That still holds. A pin trades drift for expiry, and of the two, a build that stops producing anything is safer than one that produces something and is wrong about how it was made.

**`macos-15-intel` is the last x86_64 image Actions will offer and it goes away in August 2027.** Apple discontinued the architecture and Actions follows. After that, `osx-x64` is either cross-compiled from an Apple Silicon runner — which would cost this repository the smoke test in §12.1 for that platform, since an arm64 runner cannot be relied on to execute an x86_64 binary — or dropped. **That is the sharper consequence here than next door**: Pegasus would only lose a claim about where a binary was compiled, whereas Chariot would lose the check that actually starts it. Written down because a runner image with a known end date is remembered right up until the release it breaks.

Pegasus took the identical defect from the identical file on the same day; `Pegasus_Design.md` §15.4 records it there.
