namespace EmuSen.Chariot

open System
open System.IO
open Microsoft.Data.Sqlite

/// Opening and migrating Chariot's database.
///
/// Deliberately a near-twin of the helper in the Pegasus application rather
/// than something shared. The two have no table in common - Pegasus stores
/// identities and the peers one machine has pinned, Chariot stores accounts and
/// queued post - so what would be shared is about forty lines of open-and-
/// migrate, and putting that in EmuSen.Pegasus.Core would hand SQLite to every
/// consumer of the wire protocol. The pattern is duplicated; no code is.
module Db =

    /// 2 adds the card directory and the messaging channel.
    ///
    /// The first bump that is a real migration rather than another CREATE TABLE
    /// IF NOT EXISTS: both changes add COLUMNS to tables that already exist on
    /// somebody's server, and a column cannot be added by re-running a create.
    [<Literal>]
    let SchemaVersion = 2

    let private schema =
        [ // Trust on first use, server side. The first key to claim a handle
          // owns it, which the primary key enforces rather than a rule anybody
          // has to remember - see docs/Chariot_Design.md §7.
          """
          CREATE TABLE IF NOT EXISTS accounts (
              handle      TEXT PRIMARY KEY,
              display     TEXT NOT NULL,
              public_key  BLOB NOT NULL,
              fingerprint TEXT NOT NULL,
              first_seen  TEXT NOT NULL,
              last_seen   TEXT NOT NULL
          )
          """
          // Post for somebody who was not connected, and now also post for
          // somebody who was. The payload is sealed under a key this server does
          // not have and never will - a join code on the note channel, a
          // recipient's published messaging key on the message channel - so it
          // is stored as the opaque blob it is. docs/Chariot_Design.md §6, and
          // §13 for the correction that split the two channels apart.
          //
          // The id is the delivery order, and on the note channel it is the only
          // ordering there is: nothing depends on it being right, because Yjs
          // updates are idempotent and order-independent, which is what made
          // this a queue rather than a log with all a log's problems.
          //
          // THAT ARGUMENT DOES NOT COVER THE MESSAGE CHANNEL and §13 is where it
          // is withdrawn. A message is not idempotent, not order-independent and
          // not recoverable if dropped, so on that channel the id is also what a
          // recipient acknowledges - it is the name of a row that must not be
          // deleted until somebody has said they wrote it down.
          """
          CREATE TABLE IF NOT EXISTS mailbox (
              id         INTEGER PRIMARY KEY AUTOINCREMENT,
              recipient  TEXT NOT NULL,
              sender     TEXT NOT NULL,
              payload    BLOB NOT NULL,
              queued_at  TEXT NOT NULL
          )
          """
          """
          CREATE INDEX IF NOT EXISTS mailbox_recipient ON mailbox (recipient, id)
          """
          // This server's own keypair, so it can prove to a client that it is
          // the server that client signed in to last time. One row, and the
          // CHECK is what makes that a property of the table rather than a rule
          // somebody has to remember: a second identity would mean a server
          // that could answer as either, which is exactly what pinning exists
          // to make impossible.
          //
          // THE PRIVATE KEY IS STORED UNSEALED, and that is deliberate. See
          // ServerIdentity.fs, which carries the argument beside the code that
          // writes it: this is an SSH host key, not a user key.
          """
          CREATE TABLE IF NOT EXISTS server_identity (
              id          INTEGER PRIMARY KEY CHECK (id = 1),
              handle      TEXT NOT NULL,
              public_key  BLOB NOT NULL,
              private_key BLOB NOT NULL,
              created     TEXT NOT NULL
          )
          """ ]

    /// Columns added to tables somebody is already running a server on.
    ///
    /// Apart from the schema above because these are not idempotent: SQLite
    /// raises on a column that is already there, so each is guarded by a lookup
    /// rather than by swallowing the error — which would make a real failure
    /// indistinguishable from the expected one.
    let private additions =
        // The card directory, and it is the one thing here that makes Chariot
        // party to who somebody is rather than merely where they are. What
        // stops it lying is not in this table: the messaging key is signed by
        // the identity key, and a client refuses a card whose identity key is
        // not the one it already pinned. See Types.fs in the core, and §14.
        [ "accounts", "message_key", "BLOB"
          "accounts", "message_signature", "BLOB"
          // Which channel a queued payload belongs to, so delivery policy can
          // differ between them. 0 is the note channel, which is what every row
          // written before this column existed was. Defaulting old rows to
          // notes is correct rather than convenient — messages did not exist
          // when they were written.
          "mailbox", "channel", "INTEGER NOT NULL DEFAULT 0" ]

    let private execute (connection: SqliteConnection) (sql: string) =
        use command = connection.CreateCommand()
        command.CommandText <- sql
        command.ExecuteNonQuery() |> ignore

    /// Whether a table already carries a column, from SQLite's own catalogue.
    ///
    /// PRAGMA table_info rather than selecting the column and seeing whether it
    /// throws: "did it throw" cannot tell a missing column from a missing table
    /// or a locked file, and a migration is the last place to be guessing.
    let private hasColumn (connection: SqliteConnection) (table: string) (column: string) =
        use command = connection.CreateCommand()
        command.CommandText <- $"PRAGMA table_info({table})"
        use reader = command.ExecuteReader()
        let mutable found = false

        while reader.Read() do
            if reader.GetString 1 = column then found <- true

        found

    /// Creating and opening stay one code path for everything expressible as
    /// CREATE TABLE IF NOT EXISTS, so there is no first-run branch to get
    /// wrong. The added columns run after those and are guarded individually,
    /// so a database at version 1 and one created this second end up identical
    /// rather than nearly so.
    let openAt (path: string) =
        let directory = Path.GetDirectoryName path

        if not (String.IsNullOrEmpty directory) then
            Directory.CreateDirectory directory |> ignore

        let fresh = not (File.Exists path)
        let connection = new SqliteConnection($"Data Source={path}")
        connection.Open()

        for statement in schema do
            execute connection statement

        for table, column, kind in additions do
            if not (hasColumn connection table column) then
                execute connection $"ALTER TABLE {table} ADD COLUMN {column} {kind}"

        execute connection $"PRAGMA user_version = {SchemaVersion}"

        if fresh && not (OperatingSystem.IsWindows()) then
            File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)

        connection

    let private bind (command: SqliteCommand) (name: string) (value: obj) =
        command.Parameters.AddWithValue(name, if isNull value then box DBNull.Value else value)
        |> ignore

    let executeWith (connection: SqliteConnection) (sql: string) (parameters: (string * obj) list) =
        use command = connection.CreateCommand()
        command.CommandText <- sql
        for name, value in parameters do bind command name value
        command.ExecuteNonQuery()

    /// Projects every row, so a caller never holds a live reader and cannot
    /// leak one past the connection it came from.
    let query (connection: SqliteConnection) (sql: string) (parameters: (string * obj) list) (read: SqliteDataReader -> 'a) =
        use command = connection.CreateCommand()
        command.CommandText <- sql
        for name, value in parameters do bind command name value
        use reader = command.ExecuteReader()
        [ while reader.Read() do yield read reader ]
