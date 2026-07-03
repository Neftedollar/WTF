module WTF.Host.Program

open System
open System.Runtime.InteropServices
open WTF.Core
// The four reflection/JIT-only subsystems are referenced ONLY in the default (JIT)
// build. Under -p:WtfAot=true their projects are dropped from the graph and the
// matching WTF_NO_* symbols are defined, so these opens (and every call below that
// touches them) are #if-gated to graceful no-ops. WtfConfig/config{}/keymap/manage/
// AgentTools/Protocol all live in WTF.Core and STAY in both builds. See docs/AOT.md.
#if !WTF_NO_FCS
open WTF.Config
#endif
#if !WTF_NO_PLUGINS
open WTF.Plugins
#endif
#if !WTF_NO_DESKTOP
open WTF.Desktop
#endif
#if !WTF_NO_AGENT
open WTF.Agent
#endif

// ---- the default configuration (xMonad-style, in F#) ----
// Per-workspace switch/move binds, generated for tags 1..9.
let private workspaceKeys =
    [ for i in 1..9 do
        sprintf "M-%d" i, SwitchWorkspace(string i)
        sprintf "M-S-%d" i, MoveToWorkspace(string i) ]

let private baseKeys =
    keymap {
        bind "M-Return"  (Spawn "kitty")
        bind "M-p"       (Spawn "wofi --show drun")
        bind "M-j"       (Focus NextWindow)
        bind "M-k"       (Focus PrevWindow)
        bind "M-m"       FocusMaster
        bind "M-S-Return" SwapMaster
        bind "M-S-j"     SwapNext
        bind "M-S-k"     SwapPrev
        bind "M-S-c"     CloseFocused
        bind "M-space"   NextLayout
        bind "M-t"       (SetLayout "tall")
        bind "M-w"       (SetLayout "wide")
        bind "M-b"       (SetLayout "bsp")
        bind "M-g"       (SetLayout "grid")
        bind "M-f"       (SetLayout "full")
        bind "M-S-space" ToggleFloat
        bind "M-S-f"     ToggleFullscreen
        bind "M-h"       (SetRatio 0.4)
        bind "M-l"       (SetRatio 0.6)
        bind "M-period"  IncMaster
        bind "M-comma"   DecMaster
        bind "M-equal"   IncGaps
        bind "M-minus"   DecGaps
        bind "M-Tab"     NextWorkspace
        bind "M-z"       Undo
        bind "M-S-z"     Redo
        bind "M-S-s"     SaveSession
    }

// The built-in DEFAULT configuration. This is the fallback the loader returns
// when ~/.config/wtf/config.fsx is missing/broken, and the base in safe mode.
// The live config (`cfg` below) is LOADED from the user file at startup and may
// be swapped at runtime; everything downstream reads it through `cfg.`.
let defaultConfig =
    config {
        modKey "Super"
        terminal "kitty"
        gaps 8
        defaultLayout "tall"
        keys (baseKeys @ workspaceKeys)
        manageHook (manage {
            rule (titleContains "Picture-in-Picture") FloatWindow
        })
        // Launch two terminals on startup so tiling is visible immediately.
        startup [ "kitty"; "kitty" ]
    }

// The LIVE config. Initialised to the default at module init (so the world /
// history bindings below have something to read); `main` replaces it with the
// user's loaded config before the compositor starts. `mutable` so a future
// hot-reload can swap it — keybinds read `cfg.Keys` through `Keymap.lookup`, so
// a swap re-binds instantly.
let mutable cfg = defaultConfig

// The runtime config engine (FCS-backed FsiEvaluationSession on its own worker
// thread). Created in `main`; held module-wide so `onReady` can start the
// hot-reload watcher and the socket handler can route {"eval"} REPL requests to
// the SAME worker thread. `Unchecked.defaultof` until `main` assigns it (nothing
// reads it before then). AOT build: the engine type lives in WTF.Config (dropped
// from the graph), so the field and every use of it is gated; config is the
// built-in `defaultConfig` (recompile-to-reconfigure, xMonad-style).
#if !WTF_NO_FCS
let mutable private configEngine : IConfigEngine = Unchecked.defaultof<_>
#endif

// ---- guarded host-local subsystem shims (the ONLY place gating lives) ----
// Each has a JIT body (calls the real subsystem) and an AOT no-op/fallback body,
// so the rest of Program.fs is byte-for-byte identical across both builds.

/// Load the live config: JIT evaluates ~/.config/wtf/config.fsx via FCS; the AOT
/// build has no FCS in the graph, so it returns the built-in default.
let private loadConfig () : WtfConfig =
#if WTF_NO_FCS
    defaultConfig
#else
    configEngine.Load()
#endif

/// Bless the current config.fsx as the last-good default (JIT) / no-op (AOT).
/// Returns true iff it compiled and was saved.
let private saveDefaultConfig () : bool =
#if WTF_NO_FCS
    false
#else
    configEngine.SaveDefault()
#endif

/// Start the config.fsx hot-reload watcher (JIT) / no-op (AOT — no FCS).
let private startWatching (cb: WtfConfig -> unit) : unit =
#if !WTF_NO_FCS
    configEngine.StartWatching(cb)
#else
    ignore cb
#endif

/// The live D-Bus desktop-shell snapshot spliced under "desktop" (JIT) / None (AOT).
let private desktopSnapshot () : System.Text.Json.Nodes.JsonObject option =
#if WTF_NO_DESKTOP
    None
#else
    Some(Desktop.snapshotJson ())
#endif

/// Inject a notification through our own D-Bus daemon (JIT) / no-op (AOT).
let private desktopNotify (summary: string) (body: string) : unit =
#if WTF_NO_DESKTOP
    ignore (summary, body)
#else
    Desktop.notify summary body
#endif

/// Become the D-Bus desktop shell (JIT) / no-op (AOT — no Tmds.DBus).
let private desktopStart () : unit =
#if !WTF_NO_DESKTOP
    Desktop.start ()
#else
    ()
#endif

/// Recognize + drive an XF86Audio* media keysym (JIT); AOT has no D-Bus/MPRIS so
/// media keys fall through to the chord path. Returns true if handled.
let private tryHandleMedia (sym: uint32) : bool =
#if WTF_NO_DESKTOP
    ignore sym
    false
#else
    match Models.MediaKey.ofKeysym sym with
    | Some action ->
        Desktop.sendMedia action
        true
    | None -> false
#endif

/// Load reflective layout plugins (JIT) / no-op (AOT — no AssemblyLoadContext path).
let private loadPlugins () : unit =
#if !WTF_NO_PLUGINS
    (PluginLoader.create ()).LoadAll()
#else
    ()
#endif

// ---- mutable world (the event loop is single-threaded, so this is safe) ----
let mutable world = { World.empty (Rect.create 0 0 1280 720) with Gaps = cfg.Gaps }

// The wallpaper actually applied (cfg.Wallpaper, or a forced solid color in safe
// mode). Held so onOutputResize can re-scale + re-push it to the new output size.
let mutable private activeWallpaper : Wallpaper = cfg.Wallpaper

// The STRUCTURED palette derived from the active wallpaper (E2: ricing configs
// read colors off it via RenderContext.Palette). Computed ONCE per wallpaper —
// recomputed in applyConfig on a wallpaper change / hot-reload — NOT per window
// per frame (extraction decodes + quantizes an image). Starts at the built-in
// default so it is valid before the first applyConfig wires the real one.
let mutable private activePalette : WTF.Core.Palette.Palette = WTF.Core.Palette.defaultPalette

// ---- singleton spawns (SpawnOnce) ------------------------------------------
// Skip a launch while a prior instance of the SAME command is still alive, so
// mashing a launcher keybind (Super+p) can't stack a hundred omniboxes. Liveness
// is tracked via the managed child handle (HasExited is accurate — the runtime
// reaps), keyed by the exact command string. Routed through /bin/sh -c so the
// command keeps its shell semantics, inheriting the host's WAYLAND_DISPLAY etc.
let private onceProcs = System.Collections.Generic.Dictionary<string, System.Diagnostics.Process>()
let private spawnOnce (cmd: string) =
    let alive =
        match onceProcs.TryGetValue cmd with
        | true, p -> (try not p.HasExited with _ -> false)
        | _ -> false
    if alive then
        eprintfn "WTF: SpawnOnce '%s' skipped — instance already running" cmd
    else
        // Prune exited entries so agent-driven/templated commands can't grow the
        // dictionary (and its retained Process handles) for the session lifetime.
        let dead =
            [ for KeyValue(k, p) in onceProcs do
                if (try p.HasExited with _ -> true) then k, p ]
        for k, p in dead do
            onceProcs.Remove k |> ignore
            try p.Dispose() with _ -> ()
        try
            let psi = System.Diagnostics.ProcessStartInfo("/bin/sh")
            psi.ArgumentList.Add "-c"
            psi.ArgumentList.Add cmd
            psi.UseShellExecute <- false
            match System.Diagnostics.Process.Start psi with
            | null -> ()
            | p -> onceProcs[cmd] <- p
        with ex -> eprintfn "WTF: SpawnOnce '%s' failed: %O" cmd ex

let private applyEffects effects =
    for e in effects do
        match e with
        | Arrange rects ->
            // Visibility is DERIVED from the arrange list: it contains exactly
            // the windows of the CURRENT workspace, so everything mapped but
            // absent belongs to another workspace and gets hidden. This is the
            // whole workspace-switch story on the C side (idempotent per id).
            let visible = rects |> List.map fst |> Set.ofList
            for KeyValue(id, _) in world.Windows do
                Ffi.wtf_set_hidden (id, (if Set.contains id visible then 0 else 1))
            for (id, r) in rects do
                let x, y, w, h = Scaling.configure cfg.Scale r
                Ffi.wtf_configure (id, x, y, w, h)
        | SpawnProcess cmd -> Ffi.wtf_spawn cmd
        | SpawnProcessOnce cmd -> spawnOnce cmd
        | CloseSurface id -> Ffi.wtf_close id
        | RenderOpacity o -> Ffi.wtf_set_inactive_opacity o
        | RenderAnimSpeed s -> Ffi.wtf_set_anim_speed s
        | RenderBorderWidth bw -> Ffi.wtf_set_border_width bw
        | RenderBorderColor (active, r, g, b) ->
            Ffi.wtf_set_border_color ((if active then 1 else 0), r, g, b)
        | RenderCornerRadius radius -> Ffi.wtf_set_corner_radius radius
        | RenderBlur on -> Ffi.wtf_set_blur ((if on then 1 else 0), 0, 0)
        // C side flips the surface's fullscreen protocol flag + hides its border;
        // the full-Screen geometry still arrives via the accompanying Arrange.
        | SetFullscreen (id, on) -> Ffi.wtf_set_fullscreen (id, (if on then 1 else 0))

// ---- undo/redo history (Core owns the logic, Host owns the cell) ----
let mutable history = History.create cfg.HistoryLimit world

// ---- E1: per-window dynamic appearance (border color + opacity) ----
// De-dup caches so re-evaluating on focus/map/arrange does not spam the FFI. Only
// touched when a borderColor/windowOpacity FUNCTION is configured (gated below).
let private lastBorder = System.Collections.Generic.Dictionary<int, float * float * float>()
let private lastOpacity = System.Collections.Generic.Dictionary<int, float>()
let private lastFloating = System.Collections.Generic.Dictionary<int, bool>()

/// Re-evaluate the dynamic appearance functions for every window visible on the
/// current workspace and push the per-window border-color / opacity overrides
/// (de-duped). GATED: if neither borderColor nor windowOpacity is configured this
/// is a NO-OP (zero FFI calls) and appearance stays fully on the C global path —
/// byte-for-byte today's behavior, including the live SetInactiveOpacity/
/// SetBorderColor knobs and legacy focus coloring. Per-knob gating means
/// configuring only one leaves the other entirely on the global path.
/// Runs on the loop thread (safe to call Ffi).
let private restyleWindows (w: World) : unit =
    let curWs = World.currentWorkspace w
    let ids =
        curWs.Stack
        |> Option.map Stack.toList
        |> Option.defaultValue []
    // Tell the C side which windows are FLOATING (de-duped). The compositor only
    // allows interactive (mouse) move/resize on floating windows — a tiled window's
    // size is owned by the layout, so free-resizing one collapses it. This runs
    // ALWAYS (ungated): float state can change with zero appearance functions set.
    for id in ids do
        let isFloating = Map.containsKey id curWs.Floating
        let changed =
            match lastFloating.TryGetValue id with
            | true, v -> v <> isFloating
            | _ -> true
        if changed then
            lastFloating[id] <- isFloating
            Ffi.wtf_set_floating (id, (if isFloating then 1 else 0))
    let pushBorder = cfg.BorderColorOf.IsSome
    let pushOpac = cfg.OpacityOf.IsSome
    if pushBorder || pushOpac then
        let focused = World.focusedWindow w
        for id in ids do
            match Map.tryFind id w.Windows with
            | None -> ()
            | Some info ->
                let ctx = { Window = info; Workspace = w.Current; Focused = (focused = Some id); Palette = activePalette }
                let style = Appearance.resolveWindowStyle cfg ctx
                if pushBorder then
                    let changed =
                        match lastBorder.TryGetValue id with
                        | true, v -> v <> style.BorderColor
                        | _ -> true
                    if changed then
                        lastBorder[id] <- style.BorderColor
                        let (r, g, b) = style.BorderColor
                        Ffi.wtf_set_window_border_color (id, r, g, b, 1.0)
                if pushOpac then
                    let changed =
                        match lastOpacity.TryGetValue id with
                        | true, v -> v <> style.Opacity
                        | _ -> true
                    if changed then
                        lastOpacity[id] <- style.Opacity
                        Ffi.wtf_set_window_opacity (id, style.Opacity)

/// Drop the de-dup cache entry for a window that is gone (its C toplevel is
/// destroyed, so no FFI clear is needed — this just prevents a stale-id match if
/// the compositor ever recycles the id).
let private forgetStyle (id: int) : unit =
    lastBorder.Remove id |> ignore
    lastOpacity.Remove id |> ignore
    lastFloating.Remove id |> ignore

/// Re-sync the compositor to a world that was swapped in out-of-band (undo/redo
/// /restore): re-tile and re-focus. Mirrors the onViewUnmap pattern exactly.
let private resync (w: World) =
    applyEffects [ Arrange(World.arrange w) ]
    World.focusedWindow w |> Option.iter Ffi.wtf_focus
    restyleWindows w

/// Settings-only restore: adopt a saved session's Current / Nmaster / Ratio /
/// Gaps and per-workspace layout names, but keep the *live* window set — a fresh
/// compositor has no surfaces backing the saved ids, so the stacks/Windows map
/// are intentionally dropped (the compositor re-maps real surfaces).
let private restoreSettings (saved: World) (live: World) : World =
    let layoutOf tag =
        saved.Workspaces
        |> List.tryFind (fun ws -> ws.Tag = tag)
        |> Option.map (fun ws -> ws.Layout)
    { live with
        Current = (if live.Workspaces |> List.exists (fun ws -> ws.Tag = saved.Current) then saved.Current else live.Current)
        Nmaster = saved.Nmaster
        Ratio = saved.Ratio
        Gaps = saved.Gaps
        Workspaces =
            live.Workspaces
            |> List.map (fun ws ->
                match layoutOf ws.Tag with
                | Some l -> { ws with Layout = l }
                | None -> ws) }

/// Forward hook for ReloadConfig (M-S-r / `wtfctl reload`): re-read config.fsx
/// from disk and apply it live — the same path the save-triggered watcher uses.
/// A cell because dispatch() is defined here but applyConfigReload + loadConfig
/// compose further down (they depend on the appearance/effect machinery); it is
/// wired once, at module init, right after applyConfigReload is defined. Until
/// then it is a safe no-op, so an early dispatch can't NRE.
let mutable private reloadConfigFromDisk : unit -> unit = ignore

/// Forward hook for SaveDefault (`wtfctl save-default`): validate + snapshot the
/// current config.fsx as the last-good fallback. Runs the FCS eval OFF the loop
/// thread (like reloadConfigFromDisk) so blessing never freezes the session, and
/// reports the outcome as a desktop notification. Wired at module init below.
let mutable private saveDefaultToDisk : unit -> unit = ignore

/// The single choke point for every command. History is recorded here and
/// nowhere else, so it can never desync. Undo/Redo/Save/LoadSession/ReloadConfig
/// are intercepted (the pure reducer can't see history/config); everything else
/// runs through the reducer and records an undo point iff it actually changed World.
let private dispatch (cmd: Command) : unit =
    match cmd with
    | Undo -> History.undo history |> Option.iter (fun (h, w') -> history <- h; world <- w'; resync w')
    | Redo -> History.redo history |> Option.iter (fun (h, w') -> history <- h; world <- w'; resync w')
    | ReloadConfig -> reloadConfigFromDisk ()
    | SaveDefault -> saveDefaultToDisk ()
    | SaveSession -> SessionIO.save world
    | LoadSession ->
        match SessionIO.load () with
        | Some saved ->
            let w' = restoreSettings saved world
            world <- w'
            resync w'
        | None -> ()
    | _ ->
        let prevFocus = World.focusedWindow world
        let w', effects = Reducer.apply cmd world
        if Reducer.isUndoable cmd && w' <> world then
            history <- History.push w' history
        world <- w'
        applyEffects effects
        // A command that MOVED the focus (Focus/FocusMaster/Swap*/CloseFocused/
        // workspace switch) must sync the compositor's keyboard focus + active
        // styling: applyEffects only re-tiles (Arrange), it does NOT move seat
        // focus, so without this a keyboard `Focus NextWindow` wouldn't actually
        // switch which window has keyboard input or the active border. The C-side
        // view_focus echo is guarded (no loop).
        let newFocus = World.focusedWindow world
        if newFocus <> prevFocus then
            newFocus |> Option.iter Ffi.wtf_focus
        restyleWindows world

// ---- callbacks invoked by the C compositor (C -> F#) ----

let private cstr (p: nativeint) =
    match Marshal.PtrToStringUTF8 p with
    | null -> ""
    | s -> s

let onViewMap (id: int) (appId: nativeint) (title: nativeint) : unit =
    let info = { Id = id; AppId = cstr appId; Title = cstr title; Floating = false }
    eprintfn "WTF: map   id=%d app=%s title=%s" id info.AppId info.Title
    let w', effects = Manage.onAdd cfg info world
    world <- w'
    applyEffects effects
    Ffi.wtf_focus id
    restyleWindows world
    for (wid, r) in World.arrange world do
        eprintfn "WTF:   tile id=%d -> %d,%d %dx%d" wid r.X r.Y r.Width r.Height

let onViewUnmap (id: int) : unit =
    let w', effects = Reducer.apply (RemoveWindow id) world
    world <- w'
    applyEffects effects
    forgetStyle id
    match World.focusedWindow world with
    | Some f -> Ffi.wtf_focus f
    | None -> ()
    restyleWindows world

/// A view became focused in the compositor — notably pointer click-to-focus, which
/// is entirely C-driven and would otherwise leave the brain's focus (and thus any
/// focus-dependent dynamic border/opacity) stale. Sync the brain's focus so the
/// dynamic style re-evaluates. Guard: if the brain already focuses `id` (e.g. this
/// fired as an echo of a host-initiated wtf_focus) it's a no-op. dispatch(Focus …)
/// never calls wtf_focus, so there is no C<->host focus loop.
let onViewFocus (id: int) : unit =
    if World.focusedWindow world <> Some id then
        dispatch (Focus(ById id))

// SECURITY: per-key logging of UNBOUND chords records every typed character
// (incl. text typed into password fields / 1Password) into the session log — a
// de-facto keylogger. Bound WM hotkeys (Super+...) are safe + useful to log; raw
// unbound keystrokes are gated behind WTF_DEBUG_KEYS=1 (off by default) so normal
// sessions never leak typed content while the diagnostic stays available on demand.
let private wtfDebugKeys =
    not (System.String.IsNullOrEmpty(System.Environment.GetEnvironmentVariable "WTF_DEBUG_KEYS"))

let onKey (mods: uint32) (sym: uint32) : int =
    // Media keys are the one INPUT->DBus flow. Recognize XF86Audio* keysyms
    // BEFORE the Chord path (Chord can't name them so they'd fall through to 0
    // anyway): transport keys drive the active MPRIS player, volume/mute shell
    // out to wpctl/pactl. Desktop.sendMedia is non-blocking (fires a task), so
    // the single-threaded loop never stalls. Returns 1 = handled.
    if tryHandleMedia sym then
        1
    else
    match Chord.format mods sym with
    | Some "M-S-q" ->
        SessionIO.save world // persist the session before tearing down
        Ffi.wtf_quit ()
        1 // host-level: quit the compositor
    | Some chord ->
        match Keymap.lookup cfg chord with
        | Some cmd ->
            eprintfn "WTF: key %s -> %A" chord cmd
            dispatch cmd
            1
        | None ->
            if wtfDebugKeys then eprintfn "WTF: key %s -> (unbound)" chord
            0
    | None -> 0

let onOutputResize (x: int) (y: int) (width: int) (height: int) : unit =
    // The compositor reports device (physical) px for the usable area (output
    // minus layer-shell exclusive zones); fold the output scale back to logical
    // so the brain stays in its single `lpx` coordinate space. x,y may be
    // non-zero for a top/left bar.
    let toL (v: int) : int = Px.rawL (Px.toLogical cfg.Scale (v * 1<ppx>))
    world <- { world with Screen = Rect.create (toL x) (toL y) (toL width) (toL height) }
    applyEffects [ Arrange(World.arrange world) ]
    restyleWindows world
    // Re-scale the wallpaper to the new output size (image re-scales from the
    // cached original; a solid color just re-sizes the C-side rect).
    Wallpaper.apply activeWallpaper (Px.rawL world.Screen.Width) (Px.rawL world.Screen.Height)

// ---- agent-first IPC, marshalled onto the loop thread by the bridge ----
let private bridge = Bridge.LoopBridge()

/// Set when a `restart` request arrives: the compositor tears down cleanly and
/// `main` returns the session-reload exit code (42) instead of 0, which the
/// wtf-session wrapper treats as "re-exec me" — picking up a freshly installed
/// build without a logout/reboot. See scripts/wtf-session and wtf-reload.
let [<Literal>] private RestartExitCode = 42
let mutable private restartRequested = false

/// Host-side pollers for `Script { Exec; IntervalMs }` bar widgets. One background
/// thread per unique (interval,exec) caches the latest first-line stdout; the
/// snapshot reads the cache and NEVER blocks. A per-run timeout means a hung
/// script can't wedge its poller; a failure (nonzero exit / missing binary /
/// timeout) shows empty and is logged once per script. `retain` reaps pollers no
/// longer referenced after a reload so a changing config can't leak threads. All
/// entry points run on the loop thread (snapshot build / reload).
module private BarScripts =
    open System.Diagnostics
    open System.Threading
    open System.Collections.Concurrent

    type private Poller =
        { mutable Value: string
          mutable Warned: bool
          Cts: CancellationTokenSource }

    let private table = ConcurrentDictionary<string, Poller>()
    let private keyOf (sw: ScriptWidget) = sprintf "%d|%s" sw.IntervalMs sw.Exec

    /// Run `exec` once via /bin/sh -c with a hard timeout; return its trimmed
    /// first stdout line, or None on failure/timeout.
    let private runOnce (exec: string) (timeoutMs: int) : string option =
        try
            let psi = ProcessStartInfo("/bin/sh")
            psi.ArgumentList.Add "-c"
            psi.ArgumentList.Add exec
            psi.RedirectStandardOutput <- true
            psi.RedirectStandardError <- true
            psi.UseShellExecute <- false
            use p = Process.Start psi
            let stdoutTask = p.StandardOutput.ReadToEndAsync()
            if p.WaitForExit timeoutMs then
                let text = try stdoutTask.Result with _ -> ""
                Some((text.Split('\n') |> Array.tryHead |> Option.defaultValue "").Trim())
            else
                (try p.Kill true with _ -> ())
                None
        with _ -> None

    let private loop (p: Poller) (sw: ScriptWidget) =
        // Per-run timeout clamped to the interval (with a floor so a fast poll
        // still gives the process a chance, and a cap so a wedged script can't
        // hold its slot forever).
        let timeoutMs = sw.IntervalMs |> max 500 |> min 5000
        while not p.Cts.IsCancellationRequested do
            match runOnce sw.Exec timeoutMs with
            | Some v -> p.Value <- v
            | None ->
                if not p.Warned then
                    p.Warned <- true
                    eprintfn "WTF bar: script widget '%s' failed (nonzero exit / missing / timeout); shows empty. Logged once." sw.Exec
                p.Value <- ""
            p.Cts.Token.WaitHandle.WaitOne(max 100 sw.IntervalMs) |> ignore

    /// Cached output for a Script widget; starts its poller on first sight.
    let resolve (sw: ScriptWidget) : string =
        let k = keyOf sw
        match table.TryGetValue k with
        | true, p -> p.Value
        | _ ->
            let p = { Value = ""; Warned = false; Cts = new CancellationTokenSource() }
            if table.TryAdd(k, p) then
                let t = Thread(fun () -> loop p sw)
                t.IsBackground <- true
                t.Name <- "wtf-bar-script"
                t.Start()
                ""
            else
                match table.TryGetValue k with
                | true, pp -> pp.Value
                | _ -> ""

    /// Stop and drop pollers not referenced by `keep` (called on config reload).
    let retain (keep: ScriptWidget list) : unit =
        let live = keep |> List.map keyOf |> Set.ofList
        for kv in table do
            if not (live.Contains kv.Key) then
                match table.TryRemove kv.Key with
                | true, p -> p.Cts.Cancel()
                | _ -> ()

/// Every `Script` widget referenced across a set of bars.
let private scriptsIn (bars: BarConfig list) : ScriptWidget list =
    bars
    |> List.collect (fun b -> b.Left @ b.Right)
    |> List.choose (function Script sw -> Some sw | _ -> None)

/// Build the flat read-model a `Custom` bar widget sees, from the live World and
/// (JIT only) the D-Bus desktop state. Runs on the loop thread, so reading
/// `world` is safe; Desktop.snapshot is thread-safe.
let private barContextNow () : BarContext =
    let focused =
        World.focusedWindow world |> Option.bind (fun id -> Map.tryFind id world.Windows)
    let ctx =
        { BarContext.empty with
            Windows = world.Windows |> Map.toList |> List.map snd
            FocusedTitle = focused |> Option.map (fun w -> w.Title) |> Option.defaultValue ""
            FocusedApp = focused |> Option.map (fun w -> w.AppId) |> Option.defaultValue ""
            Workspace = world.Current
            OccupiedTags =
                world.Workspaces |> List.filter (fun ws -> ws.Stack.IsSome) |> List.map (fun ws -> ws.Tag)
            Time = System.DateTime.Now }
#if WTF_NO_DESKTOP
    ctx
#else
    let d = Desktop.snapshot ()
    { ctx with
        Battery = d.Battery |> Option.map (fun b -> b.Percentage, b.State)
        Network = d.Network |> Option.map (fun n -> n.State)
        Player = d.Players |> List.tryHead |> Option.map (fun p -> p.Status, p.Title, p.Artist) }
#endif

/// The full agent-socket snapshot: world + "desktop" (live D-Bus state) + "ui"
/// (bar/omnibox styling from the LIVE cfg — this is how a config.fsx hot-reload
/// restyles the bar: its next poll simply sees the new values).
let private snapshotNow () : string =
    let extras =
        [ match desktopSnapshot () with
          | Some d -> yield ("desktop", d :> System.Text.Json.Nodes.JsonNode)
          | None -> ()
          yield ("ui", ClientUi.json activePalette (barContextNow ()) BarScripts.resolve cfg.Bars cfg.Omnibox) ]
    Protocol.snapshotLineWithNodes extras world

#if !WTF_NO_CLIENT
/// In-process embedded bars: render WTF.Client's BarRender pipeline into a scene
/// buffer via `wtf_set_bar`, reading the LIVE in-process snapshot — the exact
/// pipeline the standalone `wtf-bar` runs, minus the process/poll. The heavy
/// ImageSharp render runs on a background timer thread; only the scene mutation
/// (wtf_set_bar / wtf_clear_bar) is marshalled onto the loop thread via `bridge`.
/// A bar flipped to `embedded false` (or removed) is cleared, returning its strip.
/// AOT: WTF.Client is out of the graph, so this whole module is compiled out.
module private EmbeddedBars =
    open WTF.Client

    let private surfaces = System.Collections.Generic.Dictionary<int, Render.Surface>()
    let private lastKey = System.Collections.Generic.Dictionary<int, string>()
    let mutable private active: Set<int> = Set.empty

    let private anchorOf =
        function Top -> 0 | Bottom -> 1 | Left -> 2 | Right -> 3

    /// One render pass, OFF the loop thread. `bars`/`json`/`sw`/`sh` were read on
    /// the loop thread; `push`/`clear` marshal the scene mutation back onto it.
    let private pass (bars: BarConfig list) (json: string) (sw: int) (sh: int)
                     (push: int -> byte[] -> int -> int -> int -> int -> unit)
                     (clear: int -> unit) : unit =
        let embedded = bars |> List.indexed |> List.filter (fun (i, b) -> b.Embedded && i < 4)
        let nowActive = embedded |> List.map fst |> Set.ofList
        for id in Set.difference active nowActive do
            clear id
            surfaces.Remove id |> ignore
            lastKey.Remove id |> ignore
        active <- nowActive
        for (id, bar) in embedded do
            let ui = ClientConfig.barOfSnapshot (Some bar.Name) json
            let model = BarModel.buildWith ui.Left ui.Right System.DateTime.Now json
            let vertical = (bar.Position = Left || bar.Position = Right)
            let w = if vertical then bar.Height else sw
            let h = if vertical then sh else bar.Height
            if w > 0 && h > 0 then
                // Repaint only when the visible content or size changed.
                let key = sprintf "%dx%d|%A|%A" w h ui model
                let changed = match lastKey.TryGetValue id with true, k -> k <> key | _ -> true
                if changed then
                    lastKey[id] <- key
                    let surface =
                        match surfaces.TryGetValue id with
                        | true, s -> s
                        | _ ->
                            let s = new Render.Surface()
                            surfaces[id] <- s
                            s
                    BarRender.draw surface ui model w h
                    let bytes = surface.CopyOut(w, h)
                    push id bytes w h (anchorOf bar.Position) bar.Height

    /// Start the background render loop. `snapshotNow`, `cfg`, and `world` are all
    /// READ-ONLY here and read atomically (a mutable ref to an immutable value +
    /// the lock-guarded desktop aggregator), so building the JSON off-loop is safe;
    /// only the scene MUTATION (wtf_set_bar / wtf_clear_bar) is marshalled onto the
    /// loop thread via `bridge.Post`.
    let start () : unit =
        let push id (bytes: byte[]) w h anchor th =
            bridge.Post(Ffi.wtf_command_notify, fun () -> Ffi.wtf_set_bar(id, bytes, w, h, anchor, th))
        let clear id =
            bridge.Post(Ffi.wtf_command_notify, fun () -> Ffi.wtf_clear_bar id)
        let t =
            System.Threading.Thread(fun () ->
                while true do
                    let mutable interval = 300
                    try
                        let bars = cfg.Bars
                        let wld = world                       // one atomic ref read
                        let json = snapshotNow ()
                        let sw = Px.rawL wld.Screen.Width
                        let sh = Px.rawL wld.Screen.Height
                        match bars |> List.filter (fun b -> b.Embedded) |> List.map (fun b -> b.RefreshMs) with
                        | [] -> ()
                        | ms -> interval <- max 50 (List.min ms)
                        pass bars json sw sh push clear
                    with ex ->
                        eprintfn "WTF: embedded bar render failed: %s" ex.Message
                    System.Threading.Thread.Sleep interval)
        t.IsBackground <- true
        t.Name <- "wtf-embedded-bars"
        t.Start()
#endif

/// Apply one control-socket request ON the loop thread (safe to mutate world and
/// call wlroots), returning the resulting snapshot. A Query changes nothing.
let private handleOnLoop (line: string) : string =
    match Protocol.parseRequest line with
    | Some Protocol.Query -> snapshotNow ()
    | Some (Protocol.Act cmd) ->
        dispatch cmd
        snapshotNow ()
    // The agent tool manifest: plain data, returned verbatim so any external LLM
    // can discover + drive WTF with zero hardcoding.
    | Some Protocol.Tools -> AgentTools.manifestJson ()
    // Agent -> user: inject a notification through OUR own daemon (thread-safe via
    // the Aggregator), then reply with a fresh snapshot so the caller sees it land
    // under "desktop".
    | Some (Protocol.Notify (summary, body)) ->
        desktopNotify summary body
        snapshotNow ()
    // Hot session reload: persist the world, flag the restart, and quit. The
    // wrapper re-execs the (possibly just-rebuilt) host. Clients don't survive a
    // compositor restart — Wayland has no handover — but layout is restored from
    // the saved session on the next launch.
    | Some Protocol.Restart ->
        SessionIO.save world
        restartRequested <- true
        eprintfn "WTF: restart requested — quitting for wrapper re-exec"
        Ffi.wtf_quit ()
        snapshotNow ()
    // Opt-in in-process LLM brain. The real (async, off-loop) wiring lands in the
    // next step; until a Brain is constructed it is gracefully disabled.
    | Some (Protocol.Ask _) -> """{"error":"agent disabled (set ANTHROPIC_API_KEY)"}"""
    // Eval is routed to the FSI worker at the top-level socket handler, never the
    // loop thread — it should not arrive here.
    | Some (Protocol.Eval _) -> """{"error":"eval must run on the FSI worker"}"""
    | None -> """{"error":"unrecognized command"}"""

/// Drain callback — the compositor fires this on the loop thread after a notify.
/// Handles queued socket requests AND fire-and-forget actions (hot-reload /
/// REPL-produced config + commands marshalled from the FSI worker thread).
let onDrain () : unit =
    bridge.Drain handleOnLoop
    bridge.DrainActions()
    bridge.DrainCalls()

// ---- dynamic wallpaper timer ------------------------------------------------
// A Dynamic (.heic) wallpaper switches frames on a time-of-day schedule. The
// timer fires on a thread-pool thread, so the re-apply is Post'ed onto the loop
// thread (wlroots single-owner rule). Rescheduled after every apply; a config
// change away from Dynamic yields no next delay and the timer simply stops.
let mutable private wallpaperTimer : System.Threading.Timer option = None

let rec private scheduleWallpaperTick () =
    wallpaperTimer |> Option.iter (fun t -> t.Dispose())
    wallpaperTimer <- None
    match Wallpaper.nextSwitchDelay activeWallpaper with
    | None -> ()
    | Some delay ->
        eprintfn "WTF: dynamic wallpaper: next frame switch in %O" delay
        let t =
            new System.Threading.Timer(
                (fun _ ->
                    bridge.Post(Ffi.wtf_command_notify, fun () ->
                        eprintfn "WTF: dynamic wallpaper: switching frame"
                        Wallpaper.apply activeWallpaper (Px.rawL world.Screen.Width) (Px.rawL world.Screen.Height)
                        activePalette <- Wallpaper.paletteOf activeWallpaper
                        restyleWindows world
                        scheduleWallpaperTick ())),
                null, delay, System.Threading.Timeout.InfiniteTimeSpan)
        wallpaperTimer <- Some t

/// Push a config's appearance + input + wallpaper into the C compositor at the
/// CURRENT output size. The reusable seam shared by `onReady` and (next step)
/// the hot-reload path: anything PUSHED to C (appearance/input/wallpaper) must be
/// re-applied when the config is swapped — a plain `cfg <- ...` only re-binds
/// keys. Honors WTF_SAFE_MODE (forced minimal appearance + solid wallpaper).
/// MUST run on the loop thread (it calls Ffi/wlroots).
let applyConfig (c: WtfConfig) : unit =
    let safeMode = (System.Environment.GetEnvironmentVariable "WTF_SAFE_MODE" = "1")
    // Appearance (forced minimal in safe mode).
    let inactiveOpacity = if safeMode then 1.0 else c.InactiveOpacity
    let animSpeed       = if safeMode then 1.0 else c.AnimSpeed      // 1.0 = instant
    let cornerRadius    = if safeMode then 0   else c.CornerRadius
    let blurOn          = if safeMode then false else c.Blur
    Ffi.wtf_set_inactive_opacity inactiveOpacity
    Ffi.wtf_set_anim_speed animSpeed
    Ffi.wtf_set_border_width c.BorderWidth
    let border active hex =
        match Protocol.hexColor hex with
        | Some (r, g, b) -> Ffi.wtf_set_border_color ((if active then 1 else 0), r, g, b)
        | None -> ()
    border true c.ActiveBorder
    border false c.InactiveBorder
    Ffi.wtf_set_corner_radius cornerRadius
    Ffi.wtf_set_blur ((if blurOn then 1 else 0), 0, 0)
    // Frosted-glass frames: the border blurs the backdrop (scenefx). Eye-candy,
    // so forced off in safe mode with everything else.
    let glassOn = if safeMode then false else c.Glass
    Ffi.wtf_set_glass ((if glassOn then 1 else 0), c.GlassTint, c.GlassRefraction, (if c.GlassFrost then 1 else 0))
    // macOS-style drop shadow (scenefx). Forced off in safe mode with the rest
    // of the eye-candy; a bad ShadowColor degrades to black, never fails.
    let shadowOn = if safeMode then false else c.Shadow
    let sdx, sdy = c.ShadowOffset
    let sr, sg, sb = Protocol.hexColor c.ShadowColor |> Option.defaultValue (0.0, 0.0, 0.0)
    Ffi.wtf_set_shadow ((if shadowOn then 1 else 0), c.ShadowSigma, sr, sg, sb, c.ShadowOpacity, sdx, sdy)
    // Focus glow: colored halo around the focused frame (activeBorder hue).
    // Eye-candy => also forced off in safe mode.
    let glowOn = if safeMode then false else c.Glow
    Ffi.wtf_set_glow ((if glowOn then 1 else 0), c.GlowSigma, c.GlowIntensity)
    // Glass (frosted) bar / omnibox: tell the shim to backdrop-blur those layer
    // surfaces by namespace. Per-namespace, so all "wtf-bar" surfaces frost when
    // ANY bar sets glass (one namespace for the fleet); the omnibox is separate.
    // Off in safe mode with the rest of the eye-candy.
    let barGlass = not safeMode && (c.Bars |> List.exists (fun b -> b.Glass))
    Ffi.wtf_set_layer_blur ("wtf-bar", (if barGlass then 1 else 0))
    let omniGlass = not safeMode && c.Omnibox.Glass
    Ffi.wtf_set_layer_blur ("wtf-omnibox", (if omniGlass then 1 else 0))
    // Input devices: keyboard xkb/repeat + libinput knobs.
    // Empty xkb fields stay "" — the C side converts those to NULL for xkb defaults.
    let kb = c.Input.Keyboard
    Ffi.wtf_set_keymap (kb.Rules, kb.Model, kb.Layout, kb.Variant, kb.Options, kb.RepeatRate, kb.RepeatDelay)
    // string profile/method -> int sentinel (-1 = leave libinput default).
    let profileInt =
        function
        | "flat" -> 0
        | "adaptive" -> 1
        | _ -> -1
    let scrollMethodInt =
        function
        | "none" -> 0
        | "two-finger" -> 1
        | "edge" -> 2
        | _ -> -1
    let clickMethodInt =
        function
        | "none" -> 0
        | "button-areas" -> 1
        | "clickfinger" -> 2
        | _ -> -1
    let b (v: bool) = if v then 1 else 0
    let m = c.Input.Mouse
    let t = c.Input.Touchpad
    let mutable li = Ffi.LibinputConfig()
    li.MouseAccel <- m.AccelSpeed
    li.MouseAccelProfile <- profileInt m.AccelProfile
    li.MouseNaturalScroll <- b m.NaturalScroll
    li.Tap <- b t.Tap
    li.TapDrag <- b t.TapDrag
    li.TpNaturalScroll <- b t.NaturalScroll
    li.Dwt <- b t.DisableWhileTyping
    li.ScrollMethod <- scrollMethodInt t.ScrollMethod
    li.ClickMethod <- clickMethodInt t.ClickMethod
    li.TpAccel <- t.AccelSpeed
    li.TpAccelProfile <- profileInt t.AccelProfile
    Ffi.wtf_set_libinput_config li
    // Wallpaper into the BACKGROUND layer at the current output size. Safe mode
    // forces a plain solid color so a flaky GPU / huge image can't compound a
    // crash loop. Best-effort: a bad image logs + falls back inside Wallpaper —
    // it never throws here, so this can't block startup or a reload.
    activeWallpaper <- if safeMode then Color "#1e1e2e" else c.Wallpaper
    Wallpaper.apply activeWallpaper (Px.rawL world.Screen.Width) (Px.rawL world.Screen.Height)
    // E2: recompute the STRUCTURED palette from the (possibly changed) wallpaper
    // ONCE here — on startup, a wallpaper change, or a hot-reload — so restyleWindows
    // can read it per window WITHOUT re-decoding the image each frame. Reuses the
    // wallpaper's cached decode (Wallpaper.loadOriginal); best-effort (falls back to
    // the built-in default for a solid color / missing image).
    activePalette <- Wallpaper.paletteOf activeWallpaper
    // (Re)arm the frame-switch timer — a no-op unless the wallpaper is Dynamic.
    scheduleWallpaperTick ()
    // E1: reconcile dynamic appearance overrides. If a borderColor/windowOpacity
    // FUNCTION was REMOVED on reload, clear the per-window style (both knobs, the
    // only clear the ABI offers) for every window we previously pushed, wipe the
    // de-dup caches, then re-push whichever knob is still configured. With no
    // dynamic knobs configured (every legacy config), both caches are empty and
    // this is a pure no-op — appearance stays fully on the C global path.
    let borderDropped = c.BorderColorOf.IsNone && lastBorder.Count > 0
    let opacDropped   = c.OpacityOf.IsNone && lastOpacity.Count > 0
    if borderDropped || opacDropped then
        let ids = Set.union (Set.ofSeq lastBorder.Keys) (Set.ofSeq lastOpacity.Keys)
        for id in ids do Ffi.wtf_clear_window_style id
        lastBorder.Clear()
        lastOpacity.Clear()
    restyleWindows world

/// Apply a hot-swapped config (from the file watcher or a REPL eval) LIVE, on
/// the loop thread. Swaps the live `cfg` (so keybinds / Manage.onAdd / Keymap
/// lookups pick up the new map instantly), folds the new gaps into the world,
/// re-pushes everything derived from config (appearance/input/wallpaper via
/// `applyConfig`), then re-arranges so the new gaps/layout re-tile. Deliberately
/// does NOT re-run StartupApps (launch-only) or re-load the saved session.
/// MUST run on the loop thread (calls Ffi/wlroots through applyConfig/applyEffects).
let applyConfigReload (newCfg: WtfConfig) : unit =
    cfg <- newCfg
    world <- { world with Gaps = cfg.Gaps }
    applyConfig cfg
    applyEffects [ Arrange(World.arrange world) ]
    // Reap script-widget pollers no longer referenced by the new config so a bar
    // that drops a Script (or changes its command/interval) doesn't leak threads.
    BarScripts.retain (scriptsIn cfg.Bars)
    eprintfn "WTF: config reloaded (mod=%s, %d keybinds)" cfg.ModKey cfg.Keys.Length

// Wire the ReloadConfig hook now that loadConfig + applyConfigReload both exist:
// re-read config.fsx from disk (FCS re-eval) and apply it live. `dispatch` runs
// ON the loop thread (onKey / socket drain), but loadConfig is a FULL FCS
// recompile — seconds normally, up to the 30s engine timeout for a pathological
// config. Run it SYNCHRONOUSLY and the whole session freezes (no input, no
// frames) for that long. So: compile on a worker, Post the finished config back
// to the loop thread — the same shape as the save-watcher path. A broken config
// makes loadConfig return the last-good/default, so reload never throws the
// session away.
reloadConfigFromDisk <- fun () ->
    System.Threading.Tasks.Task.Run(fun () ->
        try
            let c = loadConfig ()
            bridge.Post(Ffi.wtf_command_notify, fun () -> applyConfigReload c)
        with ex -> eprintfn "WTF: ReloadConfig failed (config unchanged): %O" ex)
    |> ignore

// Wire SaveDefault: bless the current config.fsx off the loop thread and surface
// the result. Does NOT re-apply the config (no re-arrange) — it only persists the
// last-good fallback, so it's safe to invoke any time.
saveDefaultToDisk <- fun () ->
    System.Threading.Tasks.Task.Run(fun () ->
        try
            let ok = saveDefaultConfig ()
            let msg =
                if ok then "current config saved as default (last-good)"
                else "config has errors — default left unchanged"
            eprintfn "WTF: save-default: %s" msg
            desktopNotify "WTF" msg
        with ex -> eprintfn "WTF: SaveDefault failed: %O" ex)
    |> ignore

/// Handle an {"eval":"<code>"} socket request. JIT: routes the code to the FSI
/// worker via the config engine, marshalling any produced config/commands back
/// onto the loop thread through the bridge. AOT: FCS is not in the graph, so the
/// verb is gracefully unavailable. Module-level (sees bridge/applyConfigReload/
/// dispatch/configEngine) so the gating stays out of onReady's body.
let private handleEval (code: string) : string =
#if WTF_NO_FCS
    ignore code
    """{"error":"config eval unavailable (AOT build — recompile to reconfigure)"}"""
#else
    let jsonReply (key: string) (value: string) : string =
        let o = System.Text.Json.Nodes.JsonObject()
        o[key] <- System.Text.Json.Nodes.JsonValue.Create value
        o.ToJsonString()
    match configEngine.Eval code with
    | EvalConfig c ->
        bridge.Post(Ffi.wtf_command_notify, fun () -> applyConfigReload c)
        jsonReply "ok" "config hot-applied"
    | EvalCommands cmds ->
        bridge.Post(Ffi.wtf_command_notify, fun () -> cmds |> List.iter dispatch)
        jsonReply "ok" (sprintf "dispatched %d command(s)" cmds.Length)
    | EvalText t -> jsonReply "result" t
#endif

let onReady () : unit =
    // Safe-mode (WTF_SAFE_MODE=1): the session wrapper escalates here after a
    // crash loop. Force a minimal known-good appearance and skip startup apps so
    // a flaky GPU/driver or a heavy rice config cannot compound a crash loop.
    let safeMode = (System.Environment.GetEnvironmentVariable "WTF_SAFE_MODE" = "1")
    if safeMode then
        eprintfn "WTF: SAFE MODE active (WTF_SAFE_MODE=1) — minimal appearance, startup apps skipped"
    // The compositor is live; open the agent door and launch startup clients so
    // you see tiled windows immediately instead of an empty output.
    //
    // The socket handler runs on the per-client serving thread (NOT the loop
    // thread). A normal request goes through the bridge to the loop thread. An
    // {"eval":"<code>"} request is routed to the FSI worker thread (FSI is single-
    // threaded and lives off-loop); whatever it produces — a hot-swappable
    // WtfConfig, a Command/list, or just a text result — is then marshalled back
    // ONTO the loop thread via the bridge (Post, fire-and-forget) before replying.
    let jsonReply (key: string) (value: string) : string =
        let o = System.Text.Json.Nodes.JsonObject()
        o[key] <- System.Text.Json.Nodes.JsonValue.Create value
        o.ToJsonString()
    // Opt-in in-process LLM brain. Constructed once here (lazy + guarded on
    // ANTHROPIC_API_KEY): None when the key is absent => {"ask"} cleanly disabled,
    // nothing else affected. Each tool the model calls is routed through
    // `agentDispatch`: a Command is dispatched ON the loop thread via bridge.Call
    // (the only safe way to touch World/wlroots), returning a fresh snapshot; a
    // Notify drives the thread-safe daemon directly. The (async, slow) Brain.ask
    // itself runs on THIS per-client serving thread, never the loop/accept thread.
#if !WTF_NO_AGENT
    let agentDispatch (call: AgentTools.ToolCall) : string =
        match call with
        | AgentTools.ToCommand cmd ->
            bridge.Call(Ffi.wtf_command_notify, fun () ->
                dispatch cmd
                snapshotNow ())
        | AgentTools.ToNotify (summary, body) ->
            desktopNotify summary body
            """{"ok":"notified"}"""
    let brain = Brain.tryCreate agentDispatch
#endif
    let handle (line: string) : string =
        match Protocol.parseRequest line with
        | Some (Protocol.Eval code) -> handleEval code
        // The natural-language door. Runs the async LLM call on THIS serving thread
        // (off the loop): build the World+desktop snapshot context on the loop
        // thread, then let the brain drive the curated tools. Graceful when the
        // brain is disabled (no key). AOT: WTF.Agent is dropped from the graph, so
        // the verb is statically disabled.
        | Some (Protocol.Ask nl) ->
#if WTF_NO_AGENT
            ignore nl
            """{"error":"agent disabled (AOT build)"}"""
#else
            match brain with
            | None -> """{"error":"agent disabled (set ANTHROPIC_API_KEY)"}"""
            | Some b ->
                try
                    let snapshot =
                        bridge.Call(Ffi.wtf_command_notify, fun () ->
                            snapshotNow ())
                    jsonReply "reply" ((b.Ask snapshot nl).Result)
                with ex ->
                    jsonReply "error" (sprintf "agent failed: %s" ex.Message)
#endif
        | _ -> bridge.Submit(Ffi.wtf_command_notify, line)
    let path = Ipc.start handle
    // Be the desktop shell natively over D-Bus (notification daemon + logind /
    // UPower / MPRIS / NetworkManager clients). FIRE-AND-FORGET and best-effort:
    // it returns immediately and never blocks/crashes startup (no bus / name
    // taken / failure => degrade with a log). Started here, where the session bus
    // is available, alongside Ipc.start. AOT: no-op (no Tmds.DBus in the graph).
    desktopStart ()
    // Push the configured appearance/input/wallpaper (the reusable seam).
    applyConfig cfg
    // Start watching ~/.config/wtf/config.fsx for live hot-reload. A successful
    // re-eval (on the FSI worker thread) marshals the new config onto the loop
    // thread via the bridge; a broken edit is logged and the running config kept.
    // Skipped in safe mode (the Null engine's StartWatching is a no-op anyway).
    // AOT: no-op (no FCS in the graph).
    startWatching (fun newCfg ->
        bridge.Post(Ffi.wtf_command_notify, fun () -> applyConfigReload newCfg))
    // Restore saved settings (current tag, nmaster, ratio, gaps, per-workspace
    // layouts). Settings-only: the saved window set is dropped, since a fresh
    // compositor has no surfaces backing those ids. Re-base history afterwards.
    match SessionIO.load () with
    | Some saved ->
        world <- restoreSettings saved world
        history <- History.create cfg.HistoryLimit world
    | None -> ()
    // In-process embedded bars (default): render them in the compositor instead of
    // a separate wtf-bar process. Skipped in safe mode (minimal appearance) and in
    // the AOT build (no WTF.Client render in the graph — use the standalone client).
#if !WTF_NO_CLIENT
    if not safeMode then
        EmbeddedBars.start ()
#endif
    eprintfn "WTF: ready — agent socket at %s — spawning startup: %A" path cfg.StartupApps
    if safeMode then
        eprintfn "WTF: safe mode — skipping %d startup app(s)" cfg.StartupApps.Length
    else
        for app in cfg.StartupApps do
            Ffi.wtf_spawn app

// ---- entry point ----
[<EntryPoint>]
let main _argv =
    // Self-test fast-exit (WTF_SELFTEST=1): resolve + JIT the managed assemblies
    // and touch Core, then exit 0 WITHOUT starting the compositor. `wtf-update`
    // runs this against the freshly-copied prefix so a version-skewed or missing
    // dependency surfaces as a nonzero exit HERE — the updater rolls back to the
    // previous build instead of leaving the session a crash-loop at next login.
    if not (String.IsNullOrEmpty(Environment.GetEnvironmentVariable "WTF_SELFTEST")) then
        let w = World.empty (Rect.create 0 0 100 100)
        eprintfn "WTF selftest ok: mod=%s ws=%s gaps=%d" defaultConfig.ModKey w.Current defaultConfig.Gaps
        exit 0
    // Load the user's F# config (~/.config/wtf/config.fsx) BEFORE the compositor
    // starts — like xMonad recompiling on launch. The first FCS eval can take a
    // few seconds; this blocks briefly here (acceptable, nothing is on screen
    // yet). Graceful: a missing/broken config returns `defaultConfig`, so the WM
    // ALWAYS starts. WTF_SAFE_MODE bypasses the user file entirely (the engine
    // factory returns a no-op engine). Held for the whole run (its worker thread
    // serves future hot-reload/REPL eval). AOT: no FCS engine — loadConfig returns
    // the built-in default (recompile to reconfigure, xMonad-style).
#if !WTF_NO_FCS
    configEngine <- ConfigEngine.create defaultConfig
#endif
    eprintfn "WTF: loading config..."
    cfg <- loadConfig ()
    // Fold the loaded gaps into the world and re-base history (the module-init
    // bindings above used the default config's values).
    world <- { world with Gaps = cfg.Gaps }
    history <- History.create cfg.HistoryLimit world

    // Load layout/extension PLUGINS (#13): scan ~/.config/wtf/plugins for
    // compiled .NET assemblies implementing IWtfLayoutPlugin and register their
    // custom layouts into the live Registry — exactly like the built-ins. Done
    // HERE (after the config load, BEFORE wtf_run/the first arrange) so a config /
    // keybind that names a plugin layout (e.g. SetLayout "spiral") resolves.
    // GRACEFUL: LoadAll never throws — a bad/incompatible plugin logs + is skipped.
    // The factory no-ops under WTF_SAFE_MODE (built-in layouts only) and WTF_NO_PLUGINS
    // (the AOT build), so this single call is correct in every build/mode.
    loadPlugins ()

    // Root the delegates for the whole run so the GC can't collect them while
    // the C side holds their function pointers.
    //
    // AOT NOTE (verified-pending-ILC): these six C->F# callbacks use concrete,
    // non-generic delegate types + Marshal.GetFunctionPointerForDelegate, rooted by
    // the `let d* = ...` bindings across wtf_run + GC.KeepAlive below. This is
    // NativeAOT-compatible: ILC synthesizes the reverse-pinvoke marshalling thunk at
    // COMPILE time because every delegate type is statically known/instantiated (no
    // dynamic code, so no IL3050), and all signatures are blittable (int/uint32/
    // nativeint/double, no string/array marshalling in the ABI). [<UnmanagedCallersOnly>]
    // would shave one thunk per event but is fragile in F# (no clean &Method /
    // delegate*<> managed-function-pointer syntax), so the delegates are kept by
    // design. Finally confirmed only by the ILC pass during PublishAot.
    // EXCEPTION BARRIER for every C->F# reverse-P/Invoke callback. A managed throw
    // must NEVER unwind across the native frame — that rude-aborts the whole
    // compositor (SIGABRT -> safe-mode). Each callback runs arbitrary downstream
    // code (the reducer, user config manage-rules / appearance functions, plugin
    // layouts), so any of them can throw; we log (observability) + swallow so one
    // bad rule/keybind degrades gracefully instead of killing the session. The
    // delegate TYPES stay concrete (AOT note above still holds — the lambdas are
    // statically known targets, ILC synthesizes the thunks at compile time).
    // The catch handler itself must be throw-proof: if stderr's consumer died
    // (session logger gone, pipe closed), eprintfn throws IOException INSIDE
    // `with` — unwinding across the native frame, the exact failure the barrier
    // exists to stop. Logging is best-effort; surviving is not.
    let safeLog (msg: unit -> string) =
        try eprintfn "%s" (msg ()) with _ -> ()
    let guard name (f: unit -> unit) =
        try f () with ex -> safeLog (fun () -> sprintf "WTF: %s callback threw (ignored): %O" name ex)
    let guardKey (f: unit -> int) =
        try f () with ex -> safeLog (fun () -> sprintf "WTF: onKey callback threw (ignored): %O" ex); 0
    let dMap = Ffi.ViewMapDelegate(fun id app title -> guard "onViewMap" (fun () -> onViewMap id app title))
    let dUnmap = Ffi.ViewUnmapDelegate(fun id -> guard "onViewUnmap" (fun () -> onViewUnmap id))
    let dKey = Ffi.KeyDelegate(fun m s -> guardKey (fun () -> onKey m s))
    let dResize = Ffi.OutputResizeDelegate(fun x y w h -> guard "onOutputResize" (fun () -> onOutputResize x y w h))
    let dReady = Ffi.ReadyDelegate(fun () -> guard "onReady" onReady)
    let dDrain = Ffi.DrainDelegate(fun () -> guard "onDrain" onDrain)
    let dFocus = Ffi.ViewFocusDelegate(fun id -> guard "onViewFocus" (fun () -> onViewFocus id))

    let mutable cbs = Ffi.Callbacks()
    cbs.ViewMap <- Marshal.GetFunctionPointerForDelegate dMap
    cbs.ViewUnmap <- Marshal.GetFunctionPointerForDelegate dUnmap
    cbs.Key <- Marshal.GetFunctionPointerForDelegate dKey
    cbs.OutputResize <- Marshal.GetFunctionPointerForDelegate dResize
    cbs.Ready <- Marshal.GetFunctionPointerForDelegate dReady
    cbs.Drain <- Marshal.GetFunctionPointerForDelegate dDrain
    cbs.ViewFocus <- Marshal.GetFunctionPointerForDelegate dFocus

    eprintfn "WTF: starting compositor (mod=%s, %d keybinds)" cfg.ModKey cfg.Keys.Length
    let rc = Ffi.wtf_run cbs

    GC.KeepAlive dMap
    GC.KeepAlive dUnmap
    GC.KeepAlive dKey
    GC.KeepAlive dResize
    GC.KeepAlive dReady
    GC.KeepAlive dDrain
    GC.KeepAlive dFocus
#if !WTF_NO_FCS
    GC.KeepAlive configEngine
#endif
    // A restart request overrides the compositor's own exit code so the wrapper
    // re-execs a fresh build instead of ending the session.
    if restartRequested then RestartExitCode else rc
