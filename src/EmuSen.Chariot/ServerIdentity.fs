namespace EmuSen.Chariot

open System
open EmuSen.Pegasus

/// Chariot's own keypair, generated on first run and kept afterwards.
///
/// WHY A SERVER NEEDS ONE. Until it had one, a client's only assurance that it
/// had reached the right server was possession of the passphrase — and every
/// client holds the passphrase, so any of them could stand up a server and be
/// believed by the rest. Now Chariot sends a `Hello` carrying this key and signs
/// the nonce the client challenges it with, and the client pins it the way it
/// pins a person. `Chariot_Design.md` §7.1.
///
/// THE PRIVATE KEY IS STORED UNSEALED, and this is the decision in this file
/// most worth reading before changing.
///
/// Pegasus seals a user's private key under a password, and a test asserts it
/// never reaches disk in the clear (`Pegasus_Identity.md` §3). That works
/// because there is a person present to type the password. A server has nobody:
/// it starts at boot, unattended, and the only secrets in its environment are
/// ones it was handed by the machine it is running on. Sealing this under
/// `CHARIOT_PASSPHRASE` would put the key and the thing that opens it in the
/// same place and call it protection — and worse, the passphrase is shared with
/// every client, so it would be sealed under a secret its own users hold.
///
/// So it is stored the way an SSH host key is stored: as bytes in a file the
/// operating system protects, mode 0600, and the honest statement is that
/// anyone who can read this database is this server. Db.openAt sets the mode on
/// creation. Nothing here pretends to defend against an attacker who already
/// has the file.
module ServerIdentity =

    /// What Chariot signs in as when nothing says otherwise. It is a handle
    /// like any other and obeys the same grammar, because clients pin it in the
    /// same table they pin people in — `Pegasus_Identity.md` §7.
    let defaultHandle = Handle.Parse "chariot"

    let private read (reader: Microsoft.Data.Sqlite.SqliteDataReader) =
        {| Handle = reader.GetString 0
           Private = reader.GetFieldValue<byte[]> 2 |}

    /// Loads the identity, generating and storing one the first time.
    ///
    /// A handle that disagrees with the stored one is refused rather than
    /// quietly honoured either way. Taking the new name would present the same
    /// key under a name nobody has pinned; taking the old one would silently
    /// ignore what the operator asked for. Both are worse than saying so and
    /// stopping, which is what the Result is for.
    let loadOrCreate (dbPath: string) (handle: Handle) : Result<Identity, string> =
        use db = Db.openAt dbPath

        let existing =
            Db.query db "SELECT handle, public_key, private_key FROM server_identity WHERE id = 1" [] read

        match existing with
        | row :: _ when Handle.Parse(row.Handle) <> handle ->
            Error(
                $"this database belongs to a server called {row.Handle}, and it was asked to run as {handle.Value}. "
                + "Clients have pinned the first name against this key; running under the second would look to "
                + "every one of them like a different server. Use the original name, or a different database."
            )
        | row :: _ -> Ok(Identity.OfPrivateKey(Handle.Parse row.Handle, row.Private))
        | [] ->
            let identity = Identity.Generate handle
            let pkcs8 = identity.ExportPrivateKey()

            Db.executeWith
                db
                "INSERT INTO server_identity (id, handle, public_key, private_key, created)
                 VALUES (1, $handle, $public, $private, $created)"
                [ "$handle", box handle.Value
                  "$public", box identity.PublicKey
                  "$private", box pkcs8
                  "$created", box (DateTime.UtcNow.ToString "o") ]
            |> ignore

            Ok identity
