module WTF.Host.Program

open System
open System.Runtime.InteropServices
open WTF.Core
open WTF.Desktop

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

let cfg =
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

// ---- mutable world (the event loop is single-threaded, so this is safe) ----
let mutable world = { World.empty (Rect.create 0 0 1280 720) with Gaps = cfg.Gaps }

// The wallpaper actually applied (cfg.Wallpaper, or a forced solid color in safe
// mode). Held so onOutputResize can re-scale + re-push it to the new output size.
let mutable private activeWallpaper : Wallpaper = cfg.Wallpaper

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

/// Re-sync the compositor to a world that was swapped in out-of-band (undo/redo
/// /restore): re-tile and re-focus. Mirrors the onViewUnmap pattern exactly.
let private resync (w: World) =
    applyEffects [ Arrange(World.arrange w) ]
    World.focusedWindow w |> Option.iter Ffi.wtf_focus

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
        let w', effects = Reducer.apply cmd world
        if Reducer.isUndoable cmd && w' <> world then
            history <- History.push w' history
        world <- w'
        applyEffects effects

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
    for (wid, r) in World.arrange world do
        eprintfn "WTF:   tile id=%d -> %d,%d %dx%d" wid r.X r.Y r.Width r.Height

let onViewUnmap (id: int) : unit =
    let w', effects = Reducer.apply (RemoveWindow id) world
    world <- w'
    applyEffects effects
    match World.focusedWindow world with
    | Some f -> Ffi.wtf_focus f
    | None -> ()

let onKey (mods: uint32) (sym: uint32) : int =
    // Media keys are the one INPUT->DBus flow. Recognize XF86Audio* keysyms
    // BEFORE the Chord path (Chord can't name them so they'd fall through to 0
    // anyway): transport keys drive the active MPRIS player, volume/mute shell
    // out to wpctl/pactl. Desktop.sendMedia is non-blocking (fires a task), so
    // the single-threaded loop never stalls. Returns 1 = handled.
    match Models.MediaKey.ofKeysym sym with
    | Some action ->
        Desktop.sendMedia action
        1
    | None ->
    match Chord.format mods sym with
    | Some "M-S-q" ->
        SessionIO.save world // persist the session before tearing down
        Ffi.wtf_quit ()
        1 // host-level: quit the compositor
    | Some chord ->
        match Keymap.lookup cfg chord with
        | Some cmd ->
            dispatch cmd
            1
        | None -> 0
    | None -> 0

let onOutputResize (x: int) (y: int) (width: int) (height: int) : unit =
    // The compositor reports device (physical) px for the usable area (output
    // minus layer-shell exclusive zones); fold the output scale back to logical
    // so the brain stays in its single `lpx` coordinate space. x,y may be
    // non-zero for a top/left bar.
    let toL (v: int) : int = Px.rawL (Px.toLogical cfg.Scale (v * 1<ppx>))
    world <- { world with Screen = Rect.create (toL x) (toL y) (toL width) (toL height) }
    applyEffects [ Arrange(World.arrange world) ]
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
    let desktop = Some(Desktop.snapshotJson ())
    match Protocol.parseRequest line with
    | Some Protocol.Query -> Protocol.snapshotLineWith desktop world
    | Some (Protocol.Act cmd) ->
        dispatch cmd
        Protocol.snapshotLineWith desktop world
    | None -> """{"error":"unrecognized command"}"""

/// Drain callback — the compositor fires this on the loop thread after a notify.
let onDrain () : unit = bridge.Drain handleOnLoop

let onReady () : unit =
    // Safe-mode (WTF_SAFE_MODE=1): the session wrapper escalates here after a
    // crash loop. Force a minimal known-good appearance and skip startup apps so
    // a flaky GPU/driver or a heavy rice config cannot compound a crash loop.
    let safeMode = (System.Environment.GetEnvironmentVariable "WTF_SAFE_MODE" = "1")
    if safeMode then
        eprintfn "WTF: SAFE MODE active (WTF_SAFE_MODE=1) — minimal appearance, startup apps skipped"
    // The compositor is live; open the agent door and launch startup clients so
    // you see tiled windows immediately instead of an empty output.
    let submit line = bridge.Submit(Ffi.wtf_command_notify, line)
    let path = Ipc.start submit
    // Be the desktop shell natively over D-Bus (notification daemon + logind /
    // UPower / MPRIS / NetworkManager clients). FIRE-AND-FORGET and best-effort:
    // it returns immediately and never blocks/crashes startup (no bus / name
    // taken / failure => degrade with a log). Started here, where the session bus
    // is available, alongside Ipc.start.
    Desktop.start ()
    // Push the configured appearance into the renderer (forced minimal in safe mode).
    let inactiveOpacity = if safeMode then 1.0 else cfg.InactiveOpacity
    let animSpeed       = if safeMode then 1.0 else cfg.AnimSpeed      // 1.0 = instant
    let cornerRadius    = if safeMode then 0   else cfg.CornerRadius
    let blurOn          = if safeMode then false else cfg.Blur
    Ffi.wtf_set_inactive_opacity inactiveOpacity
    Ffi.wtf_set_anim_speed animSpeed
    Ffi.wtf_set_border_width cfg.BorderWidth
    let border active hex =
        match Protocol.hexColor hex with
        | Some (r, g, b) -> Ffi.wtf_set_border_color ((if active then 1 else 0), r, g, b)
        | None -> ()
    border true cfg.ActiveBorder
    border false cfg.InactiveBorder
    Ffi.wtf_set_corner_radius cornerRadius
    Ffi.wtf_set_blur ((if blurOn then 1 else 0), 0, 0)
    // Push the configured input devices: keyboard xkb/repeat + libinput knobs.
    // Empty xkb fields stay "" — the C side converts those to NULL for xkb defaults.
    let kb = cfg.Input.Keyboard
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
    let m = cfg.Input.Mouse
    let t = cfg.Input.Touchpad
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
    // Apply the wallpaper into the BACKGROUND layer at the current output size.
    // Safe mode forces a plain solid color so a flaky GPU / huge image can't
    // compound a crash loop. Best-effort: a bad image logs + falls back inside the
    // Wallpaper module — it never throws here, so onReady can't be blocked.
    activeWallpaper <- if safeMode then Color "#1e1e2e" else cfg.Wallpaper
    Wallpaper.apply activeWallpaper (Px.rawL world.Screen.Width) (Px.rawL world.Screen.Height)
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
    // Root the delegates for the whole run so the GC can't collect them while
    // the C side holds their function pointers.
    let dMap = Ffi.ViewMapDelegate(onViewMap)
    let dUnmap = Ffi.ViewUnmapDelegate(onViewUnmap)
    let dKey = Ffi.KeyDelegate(onKey)
    let dResize = Ffi.OutputResizeDelegate(onOutputResize)
    let dReady = Ffi.ReadyDelegate(onReady)
    let dDrain = Ffi.DrainDelegate(onDrain)

    let mutable cbs = Ffi.Callbacks()
    cbs.ViewMap <- Marshal.GetFunctionPointerForDelegate dMap
    cbs.ViewUnmap <- Marshal.GetFunctionPointerForDelegate dUnmap
    cbs.Key <- Marshal.GetFunctionPointerForDelegate dKey
    cbs.OutputResize <- Marshal.GetFunctionPointerForDelegate dResize
    cbs.Ready <- Marshal.GetFunctionPointerForDelegate dReady
    cbs.Drain <- Marshal.GetFunctionPointerForDelegate dDrain

    eprintfn "WTF: starting compositor (mod=%s, %d keybinds)" cfg.ModKey cfg.Keys.Length
    let rc = Ffi.wtf_run cbs

    GC.KeepAlive dMap
    GC.KeepAlive dUnmap
    GC.KeepAlive dKey
    GC.KeepAlive dResize
    GC.KeepAlive dReady
    GC.KeepAlive dDrain
    rc
