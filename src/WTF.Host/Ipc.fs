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

    let serve (client: Socket) =
        try
            use stream = new NetworkStream(client, true)
            use reader = new StreamReader(stream)
            use writer = new StreamWriter(stream)
            writer.AutoFlush <- true
            let mutable line = reader.ReadLine()
            while not (isNull line) do
                if line.Trim() <> "" then
                    writer.WriteLine((handle line).Replace("\n", " "))
                line <- reader.ReadLine()
        with _ -> ()

    let accept () =
        // A throw from Accept (listener disposed, fd exhaustion / ENFILE) on this
        // background thread would be an UNHANDLED exception => the whole .NET
        // process aborts. Guard each iteration so the agent socket can never take
        // down the WM; log so a persistent failure is visible.
        while true do
            try
                let client = listener.Accept()
                let t = Thread(fun () -> serve client)
                t.IsBackground <- true
                t.Start()
            with ex -> eprintfn "WTF: IPC accept failed (ignored): %O" ex

    let server = Thread(ThreadStart accept)
    server.IsBackground <- true
    server.Start()
    path
