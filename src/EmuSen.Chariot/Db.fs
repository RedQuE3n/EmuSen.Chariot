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

    [<Literal>]
    let SchemaVersion = 1

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
          """ ]

    let private execute (connection: SqliteConnection) (sql: string) =
        use command = connection.CreateCommand()
        command.CommandText <- sql
        command.ExecuteNonQuery() |> ignore

    /// Every statement is CREATE TABLE IF NOT EXISTS, so opening an existing
    /// database and creating a new one are one code path and there is no
    /// first-run branch to get wrong. A column that has to change later grows a
    /// real migration keyed on user_version.
    let openAt (path: string) =
        let directory = Path.GetDirectoryName path

        if not (String.IsNullOrEmpty directory) then
            Directory.CreateDirectory directory |> ignore

        let fresh = not (File.Exists path)
        let connection = new SqliteConnection($"Data Source={path}")
        connection.Open()

        for statement in schema do
            execute connection statement

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
