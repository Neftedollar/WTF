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

let private writeTemp (content: string) : string =
    let p = Path.Combine(Path.GetTempPath(), sprintf "wtf-cfg-%s.fsx" (Guid.NewGuid().ToString("N")))
    File.WriteAllText(p, content)
    p

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
    let cfg = load (Path.Combine(Path.GetTempPath(), sprintf "wtf-missing-%s.fsx" (Guid.NewGuid().ToString("N"))))
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
