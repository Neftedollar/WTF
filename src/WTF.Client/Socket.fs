namespace WTF.Client

open System
open System.IO
open System.Net.Sockets

/// The shared NDJSON client for the WTF control socket ($XDG_RUNTIME_DIR/wtf.sock),
/// factored out of wtfctl so the status bar and wtfctl talk to the WM the SAME way
/// (one line out, one line back). Byte-identical to wtfctl's original send/socketPath.
module Socket =

    /// $XDG_RUNTIME_DIR/wtf.sock (falls back to /tmp when the env var is unset).
    let socketPath () =
        let dir =
            match Environment.GetEnvironmentVariable "XDG_RUNTIME_DIR" with
            | null | "" -> "/tmp"
            | d -> d
        Path.Combine(dir, "wtf.sock")

    /// Send one NDJSON line and return the single-line reply. THROWS if the socket
    /// is absent/unreachable — callers that must degrade gracefully use `trySend`.
    let send (line: string) : string =
        use sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
        sock.Connect(UnixDomainSocketEndPoint(socketPath ()))
        use stream = new NetworkStream(sock, true)
        use w = new StreamWriter(stream, AutoFlush = true)
        use r = new StreamReader(stream)
        w.WriteLine line
        match r.ReadLine() with
        | null -> "(no response)"
        | s -> s

    /// Graceful variant: returns None instead of throwing when the WM/socket is
    /// not there (the bar shows what it can; it must never crash on a missing WM).
    let trySend (line: string) : string option =
        try Some(send line)
        with _ -> None
