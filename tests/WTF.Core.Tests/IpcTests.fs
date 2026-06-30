module WTF.Core.Tests.IpcTests

// Ipc had no tests. socketPath's fallback and serve's framing guarantee (one
// request line -> exactly one response line, blank lines skipped, embedded
// newlines in a reply collapsed to spaces) are the contract IPC clients rely on.
// All methods live in ONE class so xUnit runs them serially — safe to poke the
// XDG_RUNTIME_DIR env var without cross-test interference.

open System
open System.IO
open System.Net.Sockets
open System.Text
open Xunit
open WTF.Host

[<Collection("ipc-env")>]
type IpcTests() =

    let withRuntimeDir (value: string) (f: unit -> unit) =
        let prev = Environment.GetEnvironmentVariable "XDG_RUNTIME_DIR"
        try
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", value)
            f ()
        finally
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", prev)

    [<Fact>]
    member _.``socketPath falls back to /tmp when XDG_RUNTIME_DIR is unset`` () =
        withRuntimeDir null (fun () ->
            Assert.Equal("/tmp/wtf.sock", Ipc.socketPath ()))

    [<Fact>]
    member _.``socketPath falls back to /tmp when XDG_RUNTIME_DIR is empty`` () =
        withRuntimeDir "" (fun () ->
            Assert.Equal("/tmp/wtf.sock", Ipc.socketPath ()))

    [<Fact>]
    member _.``socketPath joins wtf.sock under XDG_RUNTIME_DIR`` () =
        withRuntimeDir "/run/user/1000" (fun () ->
            Assert.Equal("/run/user/1000/wtf.sock", Ipc.socketPath ()))

    // --- serve framing: one request -> one response, blanks skipped, \n collapsed.

    member private _.RoundTrip(handle: string -> string, requests: string list) : string list =
        let dir = Path.Combine(Path.GetTempPath(), "wtf-ipc-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory dir |> ignore
        let mutable path = ""
        withRuntimeDir dir (fun () -> path <- Ipc.start handle)
        use client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
        client.ReceiveTimeout <- 5000
        client.Connect(UnixDomainSocketEndPoint path)
        use stream = new NetworkStream(client, true)
        use writer = new StreamWriter(stream, Encoding.ASCII)
        writer.AutoFlush <- true
        use reader = new StreamReader(stream, Encoding.ASCII)
        for r in requests do
            writer.WriteLine r
        // Only non-blank requests get a reply; read exactly that many lines.
        let expected = requests |> List.filter (fun r -> r.Trim() <> "") |> List.length
        [ for _ in 1 .. expected -> reader.ReadLine() ]

    [<Fact>]
    member this.``a multi-line reply is collapsed to a single response line`` () =
        let replies = this.RoundTrip((fun line -> line + "\nsecond\nthird"), [ "hello" ])
        Assert.Equal<string list>([ "hello second third" ], replies)

    [<Fact>]
    member this.``blank and whitespace-only request lines are skipped`` () =
        let replies =
            this.RoundTrip(
                (fun line -> "echo:" + line),
                [ ""; "   "; "real"; "\t"; "real2" ])
        Assert.Equal<string list>([ "echo:real"; "echo:real2" ], replies)

    [<Fact>]
    member this.``one request maps to exactly one response line`` () =
        let replies = this.RoundTrip((fun line -> line.ToUpper()), [ "a"; "b"; "c" ])
        Assert.Equal<string list>([ "A"; "B"; "C" ], replies)
