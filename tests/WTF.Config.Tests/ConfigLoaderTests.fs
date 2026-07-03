module WTF.Config.Tests.ConfigLoaderTests

// Real loader tests: spin up the FCS-backed engine against a temp config.fsx and
// assert the WtfConfig it extracts — plus the graceful-fallback contract (broken
// / missing / no-binding files all return the supplied default). These exercise
// the FsiEvaluationSession load path end to end (the engine injects its own
// `#r WTF.Core` + `open WTF.Core`, so the temp file only needs the binding).

open System
open System.IO
open Xunit
open WTF.Core
open WTF.Config

// A distinctively-marked default so a fallback is unambiguous in assertions.
let private defaultCfg =
    { WtfConfig.defaults with Gaps = 99; ModKey = "DEFAULT_MARKER" }

// Each config lives in its OWN temp directory so the `config.last-good.fsx`
// sidecar the engine writes on a successful load stays isolated per test (a
// shared dir would let one test's blessed fallback mask another's default case).
let private writeTemp (content: string) : string =
    let dir =
        Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), sprintf "wtf-cfg-%s" (Guid.NewGuid().ToString("N")))).FullName
    let p = Path.Combine(dir, "config.fsx")
    File.WriteAllText(p, content)
    p

/// The last-good sidecar path the engine keeps beside a config file.
let private lastGoodOf (configPath: string) : string =
    Path.Combine(Path.GetDirectoryName configPath, "config.last-good.fsx")

let private load (path: string) : WtfConfig =
    use engine = ConfigEngine.createForPath defaultCfg path
    engine.Load()

[<Fact>]
let ``loads a valid config.fsx and reads its fields`` () =
    let path =
        writeTemp """
let wtfConfig =
    config {
        modKey "Alt"
        gaps 17
        defaultLayout "bsp"
        keys (keymap { bind "M-x" CloseFocused })
    }
"""
    let cfg = load path
    File.Delete path
    Assert.Equal("Alt", cfg.ModKey)
    Assert.Equal(17, cfg.Gaps)
    Assert.Equal("bsp", cfg.DefaultLayout)
    Assert.Contains(("M-x", CloseFocused), cfg.Keys)

[<Fact>]
let ``a broken config falls back to the default`` () =
    let path = writeTemp "let wtfConfig = this is not valid F#"
    let cfg = load path
    File.Delete path
    Assert.Equal("DEFAULT_MARKER", cfg.ModKey)
    Assert.Equal(99, cfg.Gaps)

[<Fact>]
let ``a missing config falls back to the default`` () =
    let dir = Path.Combine(Path.GetTempPath(), sprintf "wtf-missing-%s" (Guid.NewGuid().ToString("N")))
    let cfg = load (Path.Combine(dir, "config.fsx"))   // isolated dir: no last-good present
    Assert.Equal("DEFAULT_MARKER", cfg.ModKey)
    Assert.Equal(99, cfg.Gaps)

[<Fact>]
let ``a config without the wtfConfig binding falls back to the default`` () =
    let path = writeTemp "let somethingElse = config { gaps 3 }"
    let cfg = load path
    File.Delete path
    Assert.Equal("DEFAULT_MARKER", cfg.ModKey)
    Assert.Equal(99, cfg.Gaps)

// --- live REPL eval classification ---

[<Fact>]
let ``eval of a config expression is classified as a hot-swap config`` () =
    use engine = ConfigEngine.createForPath defaultCfg (writeTemp "let wtfConfig = config { gaps 1 }")
    match engine.Eval "config { gaps 42 }" with
    | EvalConfig c -> Assert.Equal(42, c.Gaps)
    | other -> Assert.Fail(sprintf "expected EvalConfig, got %A" other)

[<Fact>]
let ``eval of a single command is classified as a command`` () =
    use engine = ConfigEngine.createForPath defaultCfg (writeTemp "let wtfConfig = config { gaps 1 }")
    match engine.Eval "SetGaps 7" with
    | EvalCommands [ SetGaps 7 ] -> ()
    | other -> Assert.Fail(sprintf "expected EvalCommands [SetGaps 7], got %A" other)

[<Fact>]
let ``eval of a command list is classified as commands`` () =
    use engine = ConfigEngine.createForPath defaultCfg (writeTemp "let wtfConfig = config { gaps 1 }")
    match engine.Eval "[ IncGaps; DecGaps ]" with
    | EvalCommands [ IncGaps; DecGaps ] -> ()
    | other -> Assert.Fail(sprintf "expected EvalCommands [IncGaps; DecGaps], got %A" other)

[<Fact>]
let ``eval of a plain value returns its text`` () =
    use engine = ConfigEngine.createForPath defaultCfg (writeTemp "let wtfConfig = config { gaps 1 }")
    match engine.Eval "1 + 1" with
    | EvalText "2" -> ()
    | other -> Assert.Fail(sprintf "expected EvalText \"2\", got %A" other)

// --- hot-reload via the file watcher ---

[<Fact>]
let ``editing the config file fires the watcher with the new config`` () =
    let path = writeTemp "let wtfConfig = config { gaps 5 }"
    use engine = ConfigEngine.createForPath defaultCfg path
    engine.Load() |> ignore
    use latch = new System.Threading.ManualResetEventSlim(false)
    let received = ref None
    engine.StartWatching(fun c -> received.Value <- Some c; latch.Set())
    // Edit the file — the debounced watcher re-evaluates on the worker thread.
    File.WriteAllText(path, "let wtfConfig = config { gaps 73 }")
    let fired = latch.Wait(TimeSpan.FromSeconds 15.0)
    File.Delete path
    Assert.True(fired, "watcher did not fire within 15s")
    match received.Value with
    | Some c -> Assert.Equal(73, c.Gaps)
    | None -> Assert.Fail "no config delivered"

// --- stale-binding regression (persistent FSI session across reloads) -------

[<Fact>]
let ``a reload that removes the wtfConfig binding does NOT keep the stale value`` () =
    // The FsiEvaluationSession is persistent: once `let wtfConfig = ...` lands, a
    // later edit that RENAMES/removes the binding must not silently resolve to the
    // previous session value. Two loads on ONE engine exercise the reuse path.
    let path = writeTemp "let wtfConfig = config { gaps 5 }"
    use engine = ConfigEngine.createForPath defaultCfg path
    let first = engine.Load()
    Assert.Equal(5, first.Gaps)                       // genuinely loaded the first time
    File.WriteAllText(path, "let myConfig = config { gaps 9 }")   // renamed binding
    // Remove the blessed last-good so the fallback is the built-in default: if the
    // stale FSI binding leaked, Load would return gaps=5 even with no last-good.
    File.Delete(lastGoodOf path)
    let second = engine.Load()
    File.Delete path
    Assert.Equal("DEFAULT_MARKER", second.ModKey)     // fell back, did NOT return stale gaps=5
    Assert.Equal(99, second.Gaps)

// --- last-good fallback (save correct settings as default) -------------------

[<Fact>]
let ``a broken edit falls back to the last-good config, not the built-in default`` () =
    // A successful load blesses the source as last-good; a later broken edit then
    // degrades to THAT (your last working setup), not vanilla.
    let path = writeTemp "let wtfConfig = config { modKey \"Hyper\"; gaps 7 }"
    use engine = ConfigEngine.createForPath defaultCfg path
    let first = engine.Load()
    Assert.Equal("Hyper", first.ModKey)               // real load + bless
    Assert.True(File.Exists(lastGoodOf path), "a successful load must save a last-good")
    File.WriteAllText(path, "let wtfConfig = broken !! not F#")
    let second = engine.Load()
    File.Delete path
    Assert.Equal("Hyper", second.ModKey)              // reverted to last-good, NOT DEFAULT_MARKER
    Assert.Equal(7, second.Gaps)

[<Fact>]
let ``SaveDefault blesses a compiling config and refuses a broken one`` () =
    let path = writeTemp "let wtfConfig = config { modKey \"Meta\"; gaps 3 }"
    use engine = ConfigEngine.createForPath defaultCfg path
    Assert.True(engine.SaveDefault(), "a compiling config should be saved")
    Assert.True(File.Exists(lastGoodOf path))
    File.WriteAllText(path, "let wtfConfig = nonsense %% not F#")
    Assert.False(engine.SaveDefault(), "a broken config must NOT be blessed")
    // The good last-good is still intact and still loads.
    let reverted = engine.Load()
    File.Delete path
    Assert.Equal("Meta", reverted.ModKey)

[<Fact>]
let ``a broken hot-reload edit never invokes the callback (keeps current config)`` () =
    let path = writeTemp "let wtfConfig = config { gaps 5 }"
    use engine = ConfigEngine.createForPath defaultCfg path
    engine.Load() |> ignore
    let received = ref None
    engine.StartWatching(fun c -> received.Value <- Some c)
    File.WriteAllText(path, "let wtfConfig = this is not valid F#")
    System.Threading.Thread.Sleep 4000                // ample time for the debounced (failed) re-eval
    File.Delete path
    Assert.True(received.Value.IsNone, "callback must NOT fire on a broken edit")

// --- graceful-fallback edge cases -------------------------------------------

[<Fact>]
let ``a wtfConfig bound to a non-WtfConfig value falls back to the default`` () =
    let path = writeTemp "let wtfConfig = 5"
    let cfg = load path
    File.Delete path
    Assert.Equal("DEFAULT_MARKER", cfg.ModKey)
    Assert.Equal(99, cfg.Gaps)

[<Fact>]
let ``an empty config file falls back to the default`` () =
    let path = writeTemp ""
    let cfg = load path
    File.Delete path
    Assert.Equal("DEFAULT_MARKER", cfg.ModKey)
    Assert.Equal(99, cfg.Gaps)

[<Fact>]
let ``a partial config loads for real with CE defaults for unspecified fields`` () =
    let path = writeTemp "let wtfConfig = config { gaps 3 }"
    let cfg = load path
    File.Delete path
    Assert.Equal(3, cfg.Gaps)                         // the field we set
    Assert.Equal("Super", cfg.ModKey)                 // config{} CE default — proves it LOADED
    Assert.NotEqual<string>("DEFAULT_MARKER", cfg.ModKey)   // ...not the injected fallback

[<Fact>]
let ``an infinite-loop config times out and falls back to the default`` () =
    let prev = Environment.GetEnvironmentVariable "WTF_CONFIG_LOAD_TIMEOUT_MS"
    Environment.SetEnvironmentVariable("WTF_CONFIG_LOAD_TIMEOUT_MS", "2000")
    let path = writeTemp "let wtfConfig = config { gaps 5 }\nwhile true do System.Threading.Thread.Sleep 25"
    try
        use engine = ConfigEngine.createForPath defaultCfg path
        let sw = System.Diagnostics.Stopwatch.StartNew()
        let cfg = engine.Load()
        sw.Stop()
        Assert.Equal("DEFAULT_MARKER", cfg.ModKey)    // fell back rather than hanging
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds 20.0, "Load should have timed out, not hung")
    finally
        Environment.SetEnvironmentVariable("WTF_CONFIG_LOAD_TIMEOUT_MS", prev)
        File.Delete path

// --- live REPL eval: interaction / runtime-error / empty / persistence ------

[<Fact>]
let ``eval of a binding then a reference persists REPL state`` () =
    use engine = ConfigEngine.createForPath defaultCfg (writeTemp "let wtfConfig = config { gaps 1 }")
    match engine.Eval "let x = 5" with
    | EvalText "ok" -> ()
    | other -> Assert.Fail(sprintf "expected EvalText \"ok\", got %A" other)
    match engine.Eval "x + 1" with
    | EvalText "6" -> ()
    | other -> Assert.Fail(sprintf "expected EvalText \"6\", got %A" other)

[<Fact>]
let ``eval of code that throws at runtime returns an error text`` () =
    use engine = ConfigEngine.createForPath defaultCfg (writeTemp "let wtfConfig = config { gaps 1 }")
    match engine.Eval "failwith \"boom\"" with
    | EvalText t -> Assert.StartsWith("error:", t)
    | other -> Assert.Fail(sprintf "expected EvalText error, got %A" other)

[<Fact>]
let ``eval of empty code is graceful`` () =
    use engine = ConfigEngine.createForPath defaultCfg (writeTemp "let wtfConfig = config { gaps 1 }")
    match engine.Eval "" with
    | EvalText _ -> ()                                 // some text outcome, never throws
    | other -> Assert.Fail(sprintf "expected EvalText, got %A" other)

// --- use-after-dispose ------------------------------------------------------

[<Fact>]
let ``Eval and Load after Dispose are graceful (no throw, no hang)`` () =
    let engine = ConfigEngine.createForPath defaultCfg (writeTemp "let wtfConfig = config { gaps 1 }")
    engine.Load() |> ignore
    engine.Dispose()
    match engine.Eval "1 + 1" with                     // work queue completed -> graceful text
    | EvalText _ -> ()
    | other -> Assert.Fail(sprintf "expected EvalText, got %A" other)
    Assert.Equal("DEFAULT_MARKER", (engine.Load()).ModKey)   // graceful fallback, returns promptly

// --- StartWatching robustness -----------------------------------------------

[<Fact>]
let ``StartWatching on a non-existent config dir is a graceful no-op`` () =
    let missingDir = Path.Combine(Path.GetTempPath(), "wtf-cfg-absent-" + Guid.NewGuid().ToString("N"))
    let path = Path.Combine(missingDir, "config.fsx")
    use engine = ConfigEngine.createForPath defaultCfg path
    let ex = Record.Exception(fun () -> engine.StartWatching(fun _ -> ()))
    Assert.Null(ex)

[<Fact>]
let ``a config file CREATED after StartWatching triggers a reload`` () =
    let dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "wtf-cfg-watch-" + Guid.NewGuid().ToString("N"))).FullName
    let path = Path.Combine(dir, "config.fsx")
    use engine = ConfigEngine.createForPath defaultCfg path
    engine.Load() |> ignore                            // file absent yet -> default
    use latch = new System.Threading.ManualResetEventSlim(false)
    let received = ref None
    engine.StartWatching(fun c -> received.Value <- Some c; latch.Set())
    File.WriteAllText(path, "let wtfConfig = config { gaps 21 }")
    let fired = latch.Wait(TimeSpan.FromSeconds 15.0)
    Directory.Delete(dir, true)
    Assert.True(fired, "watcher did not fire on file creation within 15s")
    match received.Value with
    | Some c -> Assert.Equal(21, c.Gaps)
    | None -> Assert.Fail "no config delivered"

[<Fact>]
let ``a burst of rapid writes is debounced into a single reload`` () =
    let path = writeTemp "let wtfConfig = config { gaps 1 }"
    use engine = ConfigEngine.createForPath defaultCfg path
    engine.Load() |> ignore
    use latch = new System.Threading.ManualResetEventSlim(false)
    let count = ref 0
    engine.StartWatching(fun _ ->
        System.Threading.Interlocked.Increment(count) |> ignore
        latch.Set())
    for i in 1 .. 12 do                                // tight burst, well within the 200ms window
        File.WriteAllText(path, sprintf "let wtfConfig = config { gaps %d }" i)
    let fired = latch.Wait(TimeSpan.FromSeconds 15.0)
    System.Threading.Thread.Sleep 1500                 // settle: ensure no second reload follows
    File.Delete path
    Assert.True(fired, "debounced reload never fired")
    Assert.Equal(1, count.Value)

// --- path resolution --------------------------------------------------------

[<Fact>]
let ``ConfigPath.resolve honours XDG_CONFIG_HOME and falls back to ~/.config`` () =
    let prev = Environment.GetEnvironmentVariable "XDG_CONFIG_HOME"
    try
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", "/xdg/here")
        Assert.Equal(Path.Combine("/xdg/here", "wtf", "config.fsx"), ConfigPath.resolve ())
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", "")   // empty == unset
        let home = Environment.GetFolderPath Environment.SpecialFolder.UserProfile
        Assert.Equal(Path.Combine(home, ".config", "wtf", "config.fsx"), ConfigPath.resolve ())
    finally
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", prev)
