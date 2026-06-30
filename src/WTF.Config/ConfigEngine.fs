namespace WTF.Config

// =============================================================================
// Runtime F# config loader — the xMonad idea: the user's window manager is a
// PROGRAM (~/.config/wtf/config.fsx), compiled at launch via the F# Compiler
// Service and loaded into the running WM.
//
// This module now owns THREE eval paths, ALL serialized onto ONE dedicated FSI
// worker thread (FsiEvaluationSession is NOT thread-safe):
//   * Load        — the blocking startup load (xMonad recompiling on launch).
//   * StartWatching — a FileSystemWatcher hot-reloads config.fsx on edit; on a
//                     successful re-eval the host's callback applies it live.
//   * Eval        — the live REPL ({"eval":"<code>"} over the agent socket).
//
// AOT-isolation seam (Phase 4 #15): the host depends ONLY on `IConfigEngine`,
// `EvalOutcome` + the built-in default config, NEVER on FSharp.Compiler.Service
// directly. The FCS-backed `FsiConfigEngine` is the ONLY type in the codebase
// that touches FSharp.Compiler.Interactive.Shell (JIT/reflection-emit), and it
// is compiled out entirely under the WTF_NO_FCS symbol so a NativeAOT build can
// swap in `NullConfigEngine` and drop the FCS reference cleanly.
//
// THREADING: this module never touches wlroots. When a reload/eval produces a
// new WtfConfig or Commands, the host marshals the RESULT onto the wlroots loop
// thread (via the LoopBridge) — the engine only hands the value back.
// =============================================================================

open System
open WTF.Core

/// The result of a live REPL eval. The engine inspects the evaluated value and
/// classifies it so the host can act WITHOUT depending on FCS: a WtfConfig hot-
/// swaps the live config, a Command/list dispatches, anything else (a plain
/// value or diagnostics) just comes back as text for the socket reply.
type EvalOutcome =
    | EvalConfig of WtfConfig
    | EvalCommands of Command list
    | EvalText of string

/// The seam the host wires to. Implemented by the FCS-backed `FsiConfigEngine`
/// (normal JIT build) and the `NullConfigEngine` (AOT / safe-mode fallback).
type IConfigEngine =
    inherit IDisposable
    /// Blocking startup load (like xMonad recompiling on launch): returns the
    /// user's WtfConfig, or the supplied default on ANY failure. NEVER throws.
    abstract member Load : unit -> WtfConfig
    /// Begin watching the config file. On a successful re-eval the supplied
    /// callback is invoked (on the FSI worker thread) with the new config; the
    /// callback is expected to marshal application onto the loop thread. A failed
    /// edit is logged and the running config is kept (never invoked). No-op for
    /// the Null engine.
    abstract member StartWatching : (WtfConfig -> unit) -> unit
    /// Evaluate F# code on the FSI worker thread against the persistent session
    /// (which already has `open WTF.Core`). Best-effort + sandboxed-by-convention
    /// (the user's own socket). NEVER throws.
    abstract member Eval : string -> EvalOutcome

/// Resolve the user config path: $XDG_CONFIG_HOME/wtf/config.fsx, falling back
/// to ~/.config/wtf/config.fsx.
module ConfigPath =
    let resolve () : string =
        let baseDir =
            match Environment.GetEnvironmentVariable "XDG_CONFIG_HOME" with
            | null | "" ->
                IO.Path.Combine(
                    Environment.GetFolderPath Environment.SpecialFolder.UserProfile,
                    ".config")
            | d -> d
        IO.Path.Combine(baseDir, "wtf", "config.fsx")

/// No-FCS engine: always returns the injected default. Used in safe-mode (so a
/// broken config can't wedge recovery) and as the AOT fallback (WTF_NO_FCS).
type NullConfigEngine(defaultConfig: WtfConfig) =
    interface IConfigEngine with
        member _.Load() = defaultConfig
        member _.StartWatching(_) = ()
        member _.Eval(_) = EvalText "config eval unavailable (safe-mode / no-FCS build)"
        member _.Dispose() = ()

#if !WTF_NO_FCS
open System.IO
open System.Threading
open System.Threading.Tasks
open System.Collections.Concurrent
open FSharp.Compiler.Interactive.Shell
open FSharp.Compiler.Diagnostics

/// FCS-backed engine. Owns a persistent FsiEvaluationSession on a single worker
/// thread. The load contract: inject `#r <WTF.Core path>` + `open WTF.Core`,
/// evaluate the user file's TEXT (so its `let wtfConfig = config { ... }` enters
/// the interactive top level), then EvalExpression "wtfConfig" and cast the
/// reflection value. Graceful: any failure logs the FCS diagnostics to stderr
/// and the host falls back to the supplied default — the WM ALWAYS starts.
type FsiConfigEngine(defaultConfig: WtfConfig, configPath: string) =

    // The dedicated FSI worker queue. Every use of `session` runs here.
    let work = new BlockingCollection<unit -> unit>()
    let outWriter = new StringWriter()
    let errWriter = new StringWriter()
    let mutable session : FsiEvaluationSession option = None

    // Hot-reload state.
    let mutable watcher : FileSystemWatcher option = None
    let mutable onReloadCb : (WtfConfig -> unit) option = None

    let log (msg: string) = eprintfn "WTF.Config: %s" msg

    let clearWriters () =
        outWriter.GetStringBuilder().Clear() |> ignore
        errWriter.GetStringBuilder().Clear() |> ignore

    let foldDiagnostics (diags: FSharpDiagnostic[]) : string =
        if isNull (box diags) || diags.Length = 0 then ""
        else
            diags
            |> Array.map (fun d -> sprintf "    [%A] %s" d.Severity d.Message)
            |> String.concat "\n"

    // Created lazily ON the worker thread (FsiEvaluationSession is single-threaded).
    let ensureSession () : FsiEvaluationSession =
        match session with
        | Some s -> s
        | None ->
            let fsiCfg = FsiEvaluationSession.GetDefaultConfiguration()
            // `--define:WTF_RUNTIME` lets the seed config.fsx guard its dev-only
            // relative `#r` so plain `dotnet fsi` still works but the runtime
            // loader (which injects its own absolute #r below) doesn't pull in a
            // SECOND WTF.Core identity (which would break the WtfConfig cast).
            let argv =
                [| "fsi"; "--noninteractive"; "--nologo"; "--gui-"; "--define:WTF_RUNTIME" |]
            let s =
                FsiEvaluationSession.Create(
                    fsiCfg, argv, new StringReader(""), outWriter, errWriter)
            // Inject the WTF.Core reference + open ONCE; the user file then sees the
            // config{} CE builder + the config types without its own #r/open.
            let corePath = typeof<WtfConfig>.Assembly.Location
            try s.EvalInteraction(sprintf "#r @\"%s\"" corePath) with ex -> log (sprintf "ref WTF.Core failed: %s" ex.Message)
            try s.EvalInteraction("open WTF.Core") with ex -> log (sprintf "open WTF.Core failed: %s" ex.Message)
            // Also inject `#r WTF.TypeProviders.dll` (the config Type-Provider
            // assembly, #15) so a strongly-typed config can write `Apps.Firefox.AppId`
            // / `Layouts.Bsp` WITHOUT its own `#r` (its dev `#r` is guarded by
            // `#if !WTF_RUNTIME`, which is defined here). We look for it as a sibling
            // of WTF.Core.dll — exactly where the dev build and install.sh place it.
            // No project reference is needed (keeps WTF.Core/WTF.Config dependency
            // free); a missing dll (e.g. single-file publish) is silently skipped,
            // and a config that ALSO `#r`s it is harmless (FSI dedups by identity).
            if not (String.IsNullOrEmpty corePath) then
                let tpPath = Path.Combine(Path.GetDirectoryName corePath, "WTF.TypeProviders.dll")
                if File.Exists tpPath then
                    try s.EvalInteraction(sprintf "#r @\"%s\"" tpPath)
                    with ex -> log (sprintf "ref WTF.TypeProviders failed: %s" ex.Message)
            session <- Some s
            s

    // The worker loop. ALL FsiEvaluationSession access is funnelled through here.
    let worker =
        let t =
            Thread(fun () ->
                for job in work.GetConsumingEnumerable() do
                    try job () with _ -> ()
                // Tear the session down on the same thread that created it.
                match session with
                | Some s -> (try (s :> IDisposable).Dispose() with _ -> ())
                | None -> ())
        t.IsBackground <- true
        t.Name <- "wtf-fsi"
        t.Start()
        t

    /// Post a unit of work to the FSI worker thread and block for its result.
    let runSync (f: unit -> 'a) : 'a =
        let tcs = TaskCompletionSource<'a>()
        work.Add(fun () ->
            try tcs.SetResult(f ())
            with ex -> tcs.SetException ex)
        tcs.Task.Result

    /// Like `runSync` but BOUNDED: returns `None` when the worker can't accept the
    /// job (engine disposed — `work` completed) or when it doesn't finish within
    /// `timeoutMs`. Used by `Load` so a pathological user config (an infinite loop
    /// or a runaway compile) can't wedge startup forever — the WM ALWAYS starts
    /// (it falls back to the built-in default on timeout).
    let runSyncOpt (timeoutMs: int) (f: unit -> 'a) : 'a option =
        let tcs = TaskCompletionSource<'a>()
        let queued =
            try
                work.Add(fun () ->
                    try tcs.SetResult(f ())
                    with ex -> tcs.SetException ex)
                true
            with _ -> false
        if not queued then None
        elif tcs.Task.Wait(timeoutMs) then Some tcs.Task.Result
        else None

    /// Startup-load timeout (ms). Defaults to a generous 30s (no real config
    /// compiles that long); overridable via WTF_CONFIG_LOAD_TIMEOUT_MS (tests use a
    /// short value to exercise the infinite-loop fallback quickly).
    let loadTimeoutMs () =
        match Environment.GetEnvironmentVariable "WTF_CONFIG_LOAD_TIMEOUT_MS" with
        | null | "" -> 30000
        | s -> (match Int32.TryParse s with | true, v when v > 0 -> v | _ -> 30000)

    // The actual load/re-eval — runs ON the worker thread. Returns Ok config on
    // success, or Error message (so the caller distinguishes a real config from a
    // fallback: Load returns the default on Error; Reload KEEPS the current).
    let tryLoadResult () : Result<WtfConfig, string> =
        let corePath = typeof<WtfConfig>.Assembly.Location
        if String.IsNullOrEmpty corePath then
            // Single-file publish: Assembly.Location is "" so we cannot #r a real
            // path. Degrade gracefully (the dev build is unaffected).
            Error "single-file build (assembly Location is empty) — hot-reload/load unavailable"
        elif not (File.Exists configPath) then
            Error (sprintf "no config at %s" configPath)
        else
            try
                let s = ensureSession ()
                clearWriters ()
                // Invalidate any prior top-level `wtfConfig` binding BEFORE evaluating
                // the new text. The FsiEvaluationSession is PERSISTENT across reloads:
                // if a valid config bound `wtfConfig` once and the file is later edited
                // to RENAME/REMOVE that binding (or the new text fails to compile after
                // a prior success), the expression eval below would otherwise still
                // resolve `wtfConfig` to the STALE previous value and return Ok(stale).
                // Shadowing it with a null sentinel makes a removed/renamed binding read
                // back as null => Error => Load falls back / hot-reload keeps current.
                (try s.EvalInteractionNonThrowing "let wtfConfig : obj = null" |> ignore with _ -> ())
                // Feed the file's TEXT to EvalInteraction (not EvalScript): a
                // `#load`/EvalScript scopes the file's `let` bindings into a module
                // named after the file, so `wtfConfig` would not be visible at the
                // interactive top level. EvalInteraction evaluates the text in the
                // top-level session scope, so `let wtfConfig = ...` becomes a
                // top-level binding the expression eval below can read. FSI still
                // honours the file's `#if`/`#r` directives (WTF_RUNTIME is defined
                // so the seed's dev `#r` is skipped).
                let content = File.ReadAllText configPath
                let scriptResult, scriptDiags = s.EvalInteractionNonThrowing content
                match scriptResult with
                | Choice2Of2 ex ->
                    Error (sprintf "config %s failed to evaluate: %s\n%s\n%s"
                            configPath ex.Message (foldDiagnostics scriptDiags) (errWriter.ToString()))
                | Choice1Of2 _ ->
                    let exprResult, exprDiags = s.EvalExpressionNonThrowing "wtfConfig"
                    match exprResult with
                    | Choice1Of2 (Some fsiVal) ->
                        match fsiVal.ReflectionValue with
                        | :? WtfConfig as c -> Ok c
                        | other ->
                            Error (sprintf "config %s: `wtfConfig` is not a WtfConfig (got %s)"
                                    configPath (if isNull (box other) then "null" else other.GetType().FullName))
                    | Choice1Of2 None ->
                        Error (sprintf "config %s: no `wtfConfig` value found — bind your config to `let wtfConfig = config { ... }`\n%s"
                                configPath (foldDiagnostics exprDiags))
                    | Choice2Of2 ex ->
                        Error (sprintf "config %s: could not read `wtfConfig`: %s\n%s"
                                configPath ex.Message (foldDiagnostics exprDiags))
            with ex ->
                Error (sprintf "config load threw: %s" ex.Message)

    // Live REPL eval — runs ON the worker thread. Try the code as an EXPRESSION
    // first (so a `config { ... }`/Command value comes back classified); if it is
    // not an expression (a binding/`open`/statement) fall back to an interaction
    // and just report ok / the error.
    let tryEval (code: string) : EvalOutcome =
        try
            let s = ensureSession ()
            clearWriters ()
            let exprResult, exprDiags = s.EvalExpressionNonThrowing code
            match exprResult with
            | Choice1Of2 (Some v) ->
                match v.ReflectionValue with
                | :? WtfConfig as c -> EvalConfig c
                | :? (Command list) as cmds -> EvalCommands cmds
                | :? Command as cmd -> EvalCommands [ cmd ]
                | null -> EvalText "()"
                | other -> EvalText (string other)
            | _ ->
                // Not a value-yielding expression — run it as an interaction.
                let r, diags = s.EvalInteractionNonThrowing code
                match r with
                | Choice1Of2 _ -> EvalText "ok"
                | Choice2Of2 ex ->
                    EvalText (sprintf "error: %s\n%s%s%s"
                                ex.Message (foldDiagnostics exprDiags) (foldDiagnostics diags) (errWriter.ToString()))
        with ex ->
            EvalText (sprintf "eval threw: %s" ex.Message)

    // Debounce: editors fire several FS events per save (write + rename/replace),
    // so coalesce a burst into a single re-eval ~200ms after it settles.
    let doReload () =
        work.Add(fun () ->
            match tryLoadResult () with
            | Ok c ->
                log "config reloaded"
                match onReloadCb with
                | Some cb -> (try cb c with ex -> log (sprintf "reload callback failed: %s" ex.Message))
                | None -> ()
            | Error msg ->
                log (sprintf "hot-reload failed — keeping current config: %s" msg))

    let debounce =
        new Timer(TimerCallback(fun _ -> doReload ()), null, Timeout.Infinite, Timeout.Infinite)

    let bump () = try debounce.Change(200, Timeout.Infinite) |> ignore with _ -> ()

    interface IConfigEngine with
        member _.Load() =
            try
                match runSyncOpt (loadTimeoutMs ()) tryLoadResult with
                | Some(Ok c) -> log (sprintf "loaded config from %s" configPath); c
                | Some(Error msg) -> log (sprintf "%s — using built-in default config" msg); defaultConfig
                | None ->
                    log "config load timed out or worker unavailable — using built-in default config"
                    defaultConfig
            with ex ->
                log (sprintf "worker load failed: %s — using built-in default config" ex.Message)
                defaultConfig

        member _.StartWatching(cb) =
            onReloadCb <- Some cb
            if String.IsNullOrEmpty(typeof<WtfConfig>.Assembly.Location) then
                log "hot-reload unavailable in single-file build (assembly Location is empty)"
            else
                try
                    let dir = Path.GetDirectoryName configPath
                    let file = Path.GetFileName configPath
                    if not (Directory.Exists dir) then
                        log (sprintf "config dir %s does not exist — hot-reload disabled" dir)
                    else
                        // Watch the DIRECTORY for the filename so editor rename/replace
                        // (write-to-temp + atomic rename) is still caught.
                        let w = new FileSystemWatcher(dir, file)
                        w.NotifyFilter <- NotifyFilters.LastWrite ||| NotifyFilters.FileName ||| NotifyFilters.Size
                        w.Changed.Add(fun _ -> bump ())
                        w.Created.Add(fun _ -> bump ())
                        w.Renamed.Add(fun _ -> bump ())
                        w.EnableRaisingEvents <- true
                        watcher <- Some w
                        log (sprintf "watching %s for hot-reload" configPath)
                with ex ->
                    log (sprintf "could not start config watcher: %s" ex.Message)

        member _.Eval(code) =
            try runSync (fun () -> tryEval code)
            with ex -> EvalText (sprintf "eval worker failed: %s" ex.Message)

        member _.Dispose() =
            (try (match watcher with Some w -> w.Dispose() | None -> ()) with _ -> ())
            (try debounce.Dispose() with _ -> ())
            (try work.CompleteAdding() with _ -> ())
#endif

/// Factory: the host calls this with the built-in default. Returns the FCS
/// engine in the normal JIT build; the Null engine in safe-mode (WTF_SAFE_MODE=1
/// bypasses user-config load so a broken config can't wedge recovery) or when
/// FCS is compiled out (WTF_NO_FCS, the AOT build).
module ConfigEngine =

    let private safeMode () =
        Environment.GetEnvironmentVariable "WTF_SAFE_MODE" = "1"

    /// Engine for the user's standard config path ($XDG_CONFIG_HOME/wtf/config.fsx).
    let create (defaultConfig: WtfConfig) : IConfigEngine =
#if WTF_NO_FCS
        new NullConfigEngine(defaultConfig) :> IConfigEngine
#else
        if safeMode () then new NullConfigEngine(defaultConfig) :> IConfigEngine
        else new FsiConfigEngine(defaultConfig, ConfigPath.resolve ()) :> IConfigEngine
#endif

    /// Engine bound to an explicit config path (used by tests). Always the FCS
    /// engine when available (safe-mode is irrelevant for a direct path).
    let createForPath (defaultConfig: WtfConfig) (path: string) : IConfigEngine =
#if WTF_NO_FCS
        new NullConfigEngine(defaultConfig) :> IConfigEngine
#else
        new FsiConfigEngine(defaultConfig, path) :> IConfigEngine
#endif
