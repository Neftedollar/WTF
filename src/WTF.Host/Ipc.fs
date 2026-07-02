module WTF.Host.Ipc

open System
open System.IO
open System.Net.Sockets
open System.Threading

/// `$XDG_RUNTIME_DIR/wtf.sock` — the agent-first control door.
let socketPath () =
    let dir =
        match Environment.GetEnvironmentVariable "XDG_RUNTIME_DIR" with
        | null | "" -> "/tmp"
        | d -> d
    Path.Combine(dir, "wtf.sock")

/// Start the newline-delimited-JSON unix-socket server. `handle` is invoked once
/// per request line and returns the response line; it may block (internally it
/// marshals the work onto the compositor thread). Each client gets its own
/// thread; the whole thing runs in the background.
let start (handle: string -> string) : string =
    let path = socketPath ()
    (try File.Delete path with _ -> ())
    let listener =
        new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
    listener.Bind(UnixDomainSocketEndPoint path)
    listener.Listen 16

    // Cap concurrent clients: each gets a dedicated ~1 MB-stack thread, so an
    // unbounded connect loop (a stuck agent retrying) piles up threads until
    // memory runs out. Beyond the cap, close immediately — the client sees EOF.
    let maxClients = 32
    let mutable liveClients = 0

    // Bounded line reader. StreamReader.ReadLine buffers until it sees '\n', so
    // ONE client streaming gigabytes without a newline OOMs the whole
    // compositor. Real requests are small JSON lines; past the cap the client
    // is broken or hostile — drop it. None = connection done (EOF/overflow).
    let maxLineBytes = 1 <<< 20
    let readBoundedLine (stream: Stream) : string option =
        // Bytes, not chars: requests are UTF-8 (agent JSON carries non-ASCII),
        // so decode the whole line at once rather than casting byte->char.
        let buf = ResizeArray<byte>()
        let decode () = System.Text.Encoding.UTF8.GetString(buf.ToArray())
        let mutable verdict = ValueNone
        while verdict.IsNone do
            match stream.ReadByte() with
            | -1 -> verdict <- ValueSome(if buf.Count > 0 then Some(decode ()) else None)
            | 10 (* '\n' *) -> verdict <- ValueSome(Some(decode ()))
            | _ when buf.Count >= maxLineBytes ->
                eprintfn "WTF: IPC line exceeded %d bytes; dropping client" maxLineBytes
                verdict <- ValueSome None
            | b -> buf.Add(byte b)
        verdict.Value

    let serve (client: Socket) =
        try
            try
                use stream = new NetworkStream(client, true)
                use writer = new StreamWriter(stream)
                writer.AutoFlush <- true
                let mutable go = true
                while go do
                    match readBoundedLine stream with
                    | None -> go <- false
                    | Some line ->
                        if line.Trim() <> "" then
                            writer.WriteLine((handle line).Replace("\n", " "))
            with _ -> ()
        finally
            Interlocked.Decrement &liveClients |> ignore

    let accept () =
        // A throw from Accept (listener disposed, fd exhaustion / ENFILE) on this
        // background thread would be an UNHANDLED exception => the whole .NET
        // process aborts. Guard each iteration so the agent socket can never take
        // down the WM; log so a persistent failure is visible.
        while true do
            try
                let client = listener.Accept()
                if Interlocked.Increment &liveClients > maxClients then
                    Interlocked.Decrement &liveClients |> ignore
                    eprintfn "WTF: IPC client cap (%d) reached; rejecting connection" maxClients
                    client.Dispose()
                else
                    let t = Thread(fun () -> serve client)
                    t.IsBackground <- true
                    t.Start()
            with ex -> eprintfn "WTF: IPC accept failed (ignored): %O" ex

    let server = Thread(ThreadStart accept)
    server.IsBackground <- true
    server.Start()
    path
