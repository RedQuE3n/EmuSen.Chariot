module EmuSen.Chariot.Program

open System
open System.Threading

/// Chariot is a daemon, so it behaves like one: options on the command line,
/// the secret out of the environment, status on stdout, refusals on stderr, and
/// a non-zero exit when it will not start.
///
///     CHARIOT_PASSPHRASE=... chariot --port 7420 --db /var/lib/chariot/chariot.db
///
/// The passphrase is NOT an argument on purpose. Anything in argv is visible to
/// every process on the machine through ps, and a shared secret that leaks to
/// anyone who can list processes is not a shared secret.
let private usage =
    "usage: chariot [--port N] [--db PATH]\n\
     \n\
     The server passphrase is read from CHARIOT_PASSPHRASE, not from the command\n\
     line, because arguments are visible to every process through ps.\n\
     \n\
     A passphrase decides who may open a session with this server. It does not\n\
     decide who they are - that is the signature each client gives on sign-in.\n"

let rec private parse args (port, db) =
    match args with
    | "--port" :: value :: rest ->
        match Int32.TryParse value with
        | true, parsed -> parse rest (parsed, db)
        | _ -> Error $"--port wants a number, got '{value}'"
    | "--db" :: value :: rest -> parse rest (port, value)
    | "--help" :: _ -> Error usage
    | [] -> Ok(port, db)
    | unknown :: _ -> Error $"unrecognised option '{unknown}'\n\n{usage}"

[<EntryPoint>]
let main argv =
    match parse (List.ofArray argv) (7420, "chariot.db") with
    | Error why ->
        eprintfn "%s" why
        2
    | Ok(port, db) ->

    match Environment.GetEnvironmentVariable "CHARIOT_PASSPHRASE" with
    | null
    | "" ->
        eprintfn "CHARIOT_PASSPHRASE is not set, and starting without one would let anybody connect.\n\n%s" usage
        2
    | passphrase ->

    use server = new Server(port, passphrase, db)
    use cts = new CancellationTokenSource()

    server.SignedIn.Add(fun peer -> printfn "+ %s  %s" peer.Handle.Value peer.Id.Value)
    server.SignedOut.Add(fun peer -> printfn "- %s" peer.Handle.Value)
    server.Refused.Add(fun why -> eprintfn "refused: %s" why)

    Console.CancelKeyPress.Add(fun e ->
        // Ctrl-C is a request to stop, not a crash: cancel the accept loop and
        // let the process end on its own terms.
        e.Cancel <- true
        cts.Cancel())

    server.Start()
    printfn "chariot listening on %d, accounts in %s" server.Port db

    try
        server.RunAsync(cts.Token).GetAwaiter().GetResult()
    with :? OperationCanceledException ->
        ()

    printfn "chariot stopped"
    0
