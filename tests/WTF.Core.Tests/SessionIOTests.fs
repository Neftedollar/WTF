module WTF.Core.Tests.SessionIOTests

// SessionTests covers Session.toJson/ofJson (in Core) but NOT the host file IO.
// SessionIO.save/load own the atomic-write + graceful-degradation contract: a
// crash mid-write must never truncate an existing session, and load must return
// None (never throw) for an absent/garbage/version-mismatched file. We pin those
// by pointing XDG_CONFIG_HOME at a throwaway dir. One class -> serial execution.

open System
open System.IO
open Xunit
open WTF.Core
open WTF.Host

[<Collection("sessionio-env")>]
type SessionIOTests() =

    let screen = Rect.create 0 0 1920 1080
    let win id app : WindowInfo = { Id = id; AppId = app; Title = app; Floating = false }
    let worldWith n =
        [ for i in 1 .. n -> AddWindow(win i (sprintf "app%d" i)) ]
        |> fun cmds -> Reducer.applyMany cmds (World.empty screen) |> fst

    // Run `f` with XDG_CONFIG_HOME pointed at a fresh temp dir; clean up after.
    let withConfigHome (f: string -> unit) =
        let dir = Path.Combine(Path.GetTempPath(), "wtf-sess-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory dir |> ignore
        let prev = Environment.GetEnvironmentVariable "XDG_CONFIG_HOME"
        try
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir)
            f dir
        finally
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", prev)
            try Directory.Delete(dir, true) with _ -> ()

    [<Fact>]
    member _.``sessionPath lives under XDG_CONFIG_HOME/wtf`` () =
        withConfigHome (fun dir ->
            Assert.Equal(Path.Combine(dir, "wtf", "session.json"), SessionIO.sessionPath ()))

    [<Fact>]
    member _.``save then load round-trips the World`` () =
        withConfigHome (fun _ ->
            let w = worldWith 3
            SessionIO.save w
            Assert.Equal(Some w, SessionIO.load ()))

    [<Fact>]
    member _.``save round-trips a mutated World`` () =
        withConfigHome (fun _ ->
            let w, _ =
                Reducer.applyMany
                    [ Focus(ById 2); SwapMaster; SetLayout "bsp"; MoveToWorkspace "3"; SwitchWorkspace "3" ]
                    (worldWith 4)
            SessionIO.save w
            Assert.Equal(Some w, SessionIO.load ()))

    [<Fact>]
    member _.``load returns None when the file is absent`` () =
        withConfigHome (fun _ ->
            Assert.Equal(None, SessionIO.load ()))

    [<Fact>]
    member _.``load returns None on garbage without throwing`` () =
        withConfigHome (fun _ ->
            let path = SessionIO.sessionPath ()
            Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
            File.WriteAllText(path, "this is not json {{{")
            Assert.Equal(None, SessionIO.load ()))

    [<Fact>]
    member _.``load returns None on a version mismatch without throwing`` () =
        withConfigHome (fun _ ->
            let path = SessionIO.sessionPath ()
            Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
            File.WriteAllText(path, """{"schema":"wtf-session","version":99}""")
            Assert.Equal(None, SessionIO.load ()))

    [<Fact>]
    member _.``save creates the wtf directory if missing`` () =
        withConfigHome (fun dir ->
            Assert.False(Directory.Exists(Path.Combine(dir, "wtf")))
            SessionIO.save (worldWith 1)
            Assert.True(File.Exists(SessionIO.sessionPath ())))

    [<Fact>]
    member _.``save leaves no .tmp file behind (atomic move)`` () =
        withConfigHome (fun _ ->
            SessionIO.save (worldWith 2)
            let path = SessionIO.sessionPath ()
            Assert.False(File.Exists(path + ".tmp"), "stray .tmp left after save")
            Assert.True(File.Exists path))

    [<Fact>]
    member _.``save over a pre-seeded session replaces it, never empties it`` () =
        withConfigHome (fun _ ->
            let path = SessionIO.sessionPath ()
            Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
            // Pre-seed an existing (different) session.
            let old = worldWith 1
            File.WriteAllText(path, Session.toJson old)
            // Overwrite with a new world.
            let fresh = worldWith 5
            SessionIO.save fresh
            // The target is non-empty and decodes to the NEW world, not truncated.
            Assert.True((FileInfo path).Length > 0L)
            Assert.Equal(Some fresh, SessionIO.load ()))
