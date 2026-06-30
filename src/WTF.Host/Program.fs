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

let private applyEffects effects =
    for e in effects do
        match e with
        | Arrange rects ->
            for (id, r) in rects do
                let x, y, w, h = Scaling.configure cfg.Scale r
                Ffi.wtf_configure (id, x, y, w, h)
        | SpawnProcess cmd -> Ffi.wtf_spawn cmd
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

/// The single choke point for every command. History is recorded here and
/// nowhere else, so it can never desync. Undo/Redo/Save/LoadSession are
/// intercepted (the pure reducer can't see history); everything else runs
/// through the reducer and records an undo point iff it actually changed World.
let private dispatch (cmd: Command) : unit =
    match cmd with
    | Undo -> History.undo history |> Option.iter (fun (h, w') -> history <- h; world <- w'; resync w')
    | Redo -> History.redo history |> Option.iter (fun (h, w') -> history <- h; world <- w'; resync w')
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
            eprintfn "WTF: key %s -> (unbound)" chord
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

/// Apply one control-socket request ON the loop thread (safe to mutate world and
/// call wlroots), returning the resulting snapshot. A Query changes nothing.
let private handleOnLoop (line: string) : string =
    // Additive: splice the live D-Bus desktop-shell state under "desktop" so the
    // agent/bar can read notifications + battery/network/players too.
    let desktop = desktopSnapshot ()
    match Protocol.parseRequest line with
    | Some Protocol.Query -> Protocol.snapshotLineWith desktop world
    | Some (Protocol.Act cmd) ->
        dispatch cmd
        Protocol.snapshotLineWith desktop world
    // The agent tool manifest: plain data, returned verbatim so any external LLM
    // can discover + drive WTF with zero hardcoding.
    | Some Protocol.Tools -> AgentTools.manifestJson ()
    // Agent -> user: inject a notification through OUR own daemon (thread-safe via
    // the Aggregator), then reply with a fresh snapshot so the caller sees it land
    // under "desktop".
    | Some (Protocol.Notify (summary, body)) ->
        desktopNotify summary body
        Protocol.snapshotLineWith (desktopSnapshot ()) world
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
    eprintfn "WTF: config reloaded (mod=%s, %d keybinds)" cfg.ModKey cfg.Keys.Length

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
                Protocol.snapshotLineWith (desktopSnapshot ()) world)
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
                            Protocol.snapshotLineWith (desktopSnapshot ()) world)
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
    eprintfn "WTF: ready — agent socket at %s — spawning startup: %A" path cfg.StartupApps
    if safeMode then
        eprintfn "WTF: safe mode — skipping %d startup app(s)" cfg.StartupApps.Length
    else
        for app in cfg.StartupApps do
            Ffi.wtf_spawn app

// ---- entry point ----
[<EntryPoint>]
let main _argv =
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
    let dMap = Ffi.ViewMapDelegate(onViewMap)
    let dUnmap = Ffi.ViewUnmapDelegate(onViewUnmap)
    let dKey = Ffi.KeyDelegate(onKey)
    let dResize = Ffi.OutputResizeDelegate(onOutputResize)
    let dReady = Ffi.ReadyDelegate(onReady)
    let dDrain = Ffi.DrainDelegate(onDrain)
    let dFocus = Ffi.ViewFocusDelegate(onViewFocus)

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
    rc
