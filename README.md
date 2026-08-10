# Chariot

The always-on half of [Pegasus](https://github.com/RedQuE3n/EmuSen.Pegasus): the thing
that knows who is online, lets one person reach another by handle instead of by
address and port, and holds what it cannot deliver until the other side comes
back.

It is deliberately **not a peer.** It never holds a note, never merges an
update, and cannot read a word either party writes.

## Why it exists

Two people can already pair Pegasus directly: one reads out an address, a port
and a join code, and the other types all three in. The port is assigned by the
operating system and differs every session, so the ritual is repeated in full
every time — and neither of them can start until both are at the keyboard.

Chariot removes two of the three. The address is stable because the server does
not move; the port belongs to the server rather than to a session. What is left
is the join code, and that is not an oversight — it is the key the notes are
sealed under, and a server that could derive it would be a server that could
read them. The rest of the ritual is a buddy list: sign in, see who else is
there, pick somebody and open a note with them by name.

The other half is the mailbox. A peer that is offline when you write no longer
means the writing waits for a meeting; Chariot holds the sealed payloads and
hands them over on reconnect.

## What it can and cannot see

This is the part worth reading before running one, and it is stated here rather
than left to the design record because someone deciding whether to trust a
server should not have to go and find it.

**It cannot read your notes.** Note traffic is sealed under the join code, which
Chariot never has and has no way to derive. It is forwarded as the opaque blob
it arrived as, and a test reads the database directly to assert that what is
queued cannot be opened by the process holding it.

**It necessarily learns metadata.** Who is connected, who sends to whom, when,
and how many bytes. Routing cannot work without a destination it can read, so
this is unavoidable rather than an oversight. Anyone who needs the routing
itself hidden wants an onion router, not this — `docs/Chariot_Design.md` §5.

**Anyone who can read the database is this server.** Chariot's own private key
is stored unsealed, mode 0600, the way an SSH host key is stored and for the
reason SSH stores it that way: a server starts at boot with nobody present to
type a password. Sealing it under `CHARIOT_PASSPHRASE` would put the key and the
thing that opens it in the same environment — and that passphrase is shared with
every client, so the server's key would be sealed under a secret its own users
hold. Pegasus seals a *user's* key and has a test asserting it never reaches
disk in the clear; the difference is not an inconsistency, it is the same
reasoning applied to a machine with no user at the keyboard. §7.1.

**A handle is owned by the first key to claim it.** Trust on first use, server
side, because there is no authority to ask and this project is deliberately not
going to run one. A handle claimed by an impostor before its rightful owner ever
connects is that impostor's handle from then on, and nothing at this layer can
tell the difference. §7.

## Routing without reading

A relayed frame carries a plaintext envelope outside the seal, and which
envelope it is decides what Chariot may open at all:

| Envelope | Means | Chariot |
|---|---|---|
| `Direct` | addressed to Chariot — sign-in, rosters | opens it; it has to |
| `ToHandle` | somebody's note traffic | forwards it without decoding |
| `FromHandle` | Chariot's stamp naming the sender on delivery | writes it; a client that sends one is refused |

`FromHandle` exists because an update is opaque bytes and says nothing about who
wrote it — a client with two correspondents could not otherwise tell their
traffic apart. It is the relay's word rather than proof, which is exactly why a
client is not allowed to write one.

## Sign-in

The exchange is deliberately the same one two Pegasus peers use — a `Hello`
carrying a public key, a challenge each way, a proof each way — so what stands
between a stranger and the roster is code the Pegasus suite already exercises
rather than a second implementation written for a server.

Chariot proves itself too, which it did not for the first three passes. It used
to answer a client's challenge with an HMAC over the passphrase key: a proof
that it held the passphrase, which every client holds, and therefore a proof of
nothing. It now has a keypair of its own, sends it in `Hello`, and signs the
client's nonce with it; the client pins that key exactly the way it pins a
person, and refuses a change afterwards. §7.1.

Once both ends have proved themselves they agree an ephemeral session key, each
signing its ephemeral with the identity it just proved. The passphrase is
therefore a doorbell rather than a key: it decides who may open a session, and
it is not what anything is sealed under once one has started. Before this,
holding the passphrase meant being able to read every other client's roster off
the wire. §7.2.

## Building

    dotnet build

One thing is not yet as simple as that. `EmuSen.Pegasus.Core` — the wire types,
the codec, the sealed envelope and the identity primitives, shared so that there
is one implementation of the protocol rather than two kept in step by hand — is
consumed as a package, because a `ProjectReference` cannot cross a repository
boundary. It is not on a feed yet, so it is hand-packed from a checkout of
Pegasus into `local-packages/`:

    dotnet pack src/EmuSen.Pegasus.Core -c Release -o <chariot>/local-packages

There is a trap in that, and it will cost somebody an afternoon: repacking the
**same version** does not propagate. NuGet caches by id and version, so a
rebuilt 0.1.0 is ignored in favour of the copy already extracted under
`~/.nuget/packages`, and the build then fails on code you just wrote as though
it did not exist. `rm -rf ~/.nuget/packages/emusen.pegasus.core` after packing.
`NuGet.config` carries the same warning where somebody will actually hit it.

There is no web stack here at all, and that is the point of speaking the Pegasus
frame protocol over TCP: no ASP.NET Core, no SignalR, no Entity Framework, no
OpenAPI. The dependencies are the shared core, `Microsoft.Data.Sqlite`, and an
explicit `SQLitePCLRaw.bundle_e_sqlite3` 3.0.5 pin that must stay beside it —
alone, SQLite resolves a transitive 2.1.11 carrying a high-severity advisory.
§3 records what dropping the web stack bought and what it deferred with it.

## Running it

    CHARIOT_PASSPHRASE=hunter2 dotnet run --project src/EmuSen.Chariot -- --port 7420 --db chariot.db

Both options have defaults — port 7420, `chariot.db` beside the working
directory — so the passphrase is the only thing that has to be supplied.

| Setting | Where from | Default |
|---|---|---|
| Passphrase | `CHARIOT_PASSPHRASE` | none; refuses to start without it |
| Handle | `CHARIOT_HANDLE` | `chariot` |
| Port | `--port` | 7420 |
| Database | `--db` | `chariot.db` |

**The passphrase is not a command-line argument on purpose.** Anything in argv
is visible to every process on the machine through `ps`, and a shared secret
that leaks to anyone who can list processes is not a shared secret.

On startup it prints its handle and key fingerprint. That is not decoration: a
client refusing to connect means the key it pinned has changed, and reading the
fingerprint aloud is the only way for a person to tell "we rebuilt the server"
from "that is not our server". Arrivals, departures and refusals go to the
console as they happen; Ctrl-C is a request to stop rather than a crash.

The handle is fixed on first run and stored beside the key. A database asked to
run under a different name is **refused rather than renamed**, and the process
exits non-zero saying why: taking the new name would present the same key under
a name nobody has pinned, and keeping the old one would silently ignore what the
operator asked for. Both are worse than stopping. §7.1.

From the other end, this is the buddy panel in the Pegasus window — type the
address and passphrase, click **Sign in**, and everyone else signed in appears
in the list.

## What it keeps

    accounts          which public key owns which handle, first seen, last seen
    mailbox           sealed payloads for somebody who was not connected
    server_identity   this server's own keypair; one row, enforced by a CHECK

Presence is in memory and nowhere else, on purpose. A roster that survived a
restart would be a list of people who are not there, which is worse than no
list, and the truth is one reconnect away.

The mailbox is bounded — 512 payloads per recipient, dropping **oldest** first,
and nothing older than fourteen days, pruned when somebody signs in rather than
by a scheduler a server for two people should not need. Post addressed to a
handle that has never signed in here is refused rather than queued, or any
client could fill the disk by writing to names it invented.

A cap looks alarming against a project whose founding constraint is that no
party may lose information, and it is not, for a reason worth stating plainly:
**every peer keeps a complete replica on its own disk.** A sender that handed
work to Chariot has not given it away. If a queued blob is dropped the two
replicas converge the next time both peers are online together. What is lost is
promptness, not content. The mailbox is liveness, not durability — §6.1.

Everything hard about a message queue is absent here, and none of it is
cleverness on this end: the payloads are Yjs updates, so applying them is
idempotent and order-independent. No ordering to enforce, no exactly-once, no
acknowledgements, no deduplication. Hand over everything undelivered and be
correct. It is the single largest simplification in the design and it comes
entirely from a decision made long before Chariot existed. §6.

## Tests

    dotnet test

20 tests, and they pass. Every one drives a real listener over a real loopback
socket, because a server that has only been tested against a mock is a server
nobody has connected to. They cover the sign-in exchange and a client that never
completes it, two clients seeing each other arrive and leave, a roster that never
contains the person reading it, a wrong passphrase never reaching sign-in, a
second key refused for a registered handle and the accounts table outliving the
process, a payload handed straight over and a payload that waits for its
recipient to come back, the queue's bound and its refusal of invented names, a
client forbidden from stamping a delivery as somebody else's, the server signing
its own challenge and keeping its key across a restart, a database refusing to be
renamed, a passphrase holder unable to read what the server says after sign-in,
and one person signed in from two places at once with a closed laptop not taking
them out of anybody's buddy list.

A test that cannot fail is not a test, so the guards were watched failing first.
Pass 5's three: storing a payload re-wrapped instead of as it arrived reddens the
test that reads the database directly; trimming the newest instead of the oldest
reddens the bound; delivering without clearing reddens the round trip. Pass 7's
four: minting a fresh server key on every start reddens the restart test; never
switching off the door key reddens six at once; displacing a second connection
for a handle reddens both multi-device tests; treating any departure as a
departure reddens the one about the closed laptop.

One test was found to have been passing on timing rather than on a guarantee —
it posted to a handle the moment that handle's socket was disposed, and a
payload for a connection the server has not yet noticed is gone gets handed to a
dying socket and dropped instead of queued. It now waits for the departure.
Recorded rather than quietly fixed, because a test that passes for the wrong
reason is worth knowing about. §9.5.

## Documentation

`docs/Chariot_Design.md` is the record: what the fork contributed and why none of
its code survives, why there is no web stack, the envelope, the mailbox
argument, identity and key storage, and a pass-by-pass table of what was
delivered and what proved it. It cites `Pegasus_Sync.md`, `Pegasus_Identity.md`
and `Pegasus_Design.md` in the Pegasus repository for the protocol itself.

§4 is a **correction** to `Pegasus_Sync.md` §1, which predicted that a relay
would simply be "a peer that never disconnects". That is only true of a relay
allowed to read your notes: merging an update requires its plaintext, so a relay
holding a replica must be able to decrypt everything both parties write. Keeping
the sealing means Chariot cannot merge, which means it is not a peer. The
prediction was reasonable and it was wrong, and the section it was made in has
been rewritten rather than left standing.

## Credit

Chariot is a hard fork of `cheshire-84/8BB-MSGR.NET`, offered to this project
after being set aside. What was kept is an idea and not a line of code: 73 lines
of `ChatHub.cs` keeping a live registry of who is connected and republishing it
to everyone whenever it changes. That is the presence model, it is the right
one, and getting to it is the hard part. Everything else in the fork was built
for a different application — everything broadcast to all, no authentication, a
chat payload where Pegasus moves sealed bytes, nothing persisted, no tests — and
the C# was deleted once F# replaced it. "We forked a repository" and "we kept
fifty lines of an idea from it" are different claims and only the second is
true. §2.

## What Chariot will not do

- **Not read notes.** A future decision giving it that power is a rewrite of the
  design record, not an enhancement.
- **No REST API and no web UI.** The fork's endpoints are not being ported.
- **Not an account authority.** It knows handles and keys well enough to route
  and to refuse impostors, and is not a login service for anything else.
- **Not groups, yet.** A note is between two people; Chariot could lift that and
  deliberately does not.

Deferred with the web stack rather than rejected: WebSocket transport, which is
far friendlier to NAT and to corporate proxies than a raw TCP port and keeps a
browser client possible. The frame layout is transport-agnostic so that argument
stays about transport. §3.

## Licence

GPL-3.0-or-later, as a consequence of linking `EmuSen.Pegasus.Core`, which is
published under it. **There is no `LICENSE` file in this repository yet** — that
is a gap rather than a statement, and it should be closed with the same text
Pegasus carries.
