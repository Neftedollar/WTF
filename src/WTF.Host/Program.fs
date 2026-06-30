module WTF.Host.Program

open System
open System.Runtime.InteropServices
open WTF.Core

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
        bind "M-h"       (SetRatio 0.4)
        bind "M-l"       (SetRatio 0.6)
        bind "M-period"  IncMaster
        bind "M-comma"   DecMaster
        bind "M-equal"   IncGaps
        bind "M-minus"   DecGaps
        bind "M-Tab"     NextWorkspace
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

let private applyEffects effects =
    for e in effects do
        match e with
        | Arrange rects -> for (id, r) in rects do Ffi.wtf_configure (id, r.X, r.Y, r.Width, r.Height)
        | SpawnProcess cmd -> Ffi.wtf_spawn cmd
        | CloseSurface id -> Ffi.wtf_close id
        | RenderOpacity o -> Ffi.wtf_set_inactive_opacity o
        | RenderAnimSpeed s -> Ffi.wtf_set_anim_speed s
        | RenderBorderWidth bw -> Ffi.wtf_set_border_width bw
        | RenderBorderColor (active, r, g, b) ->
            Ffi.wtf_set_border_color ((if active then 1 else 0), r, g, b)
        | RenderCornerRadius radius -> Ffi.wtf_set_corner_radius radius
        | RenderBlur on -> Ffi.wtf_set_blur ((if on then 1 else 0), 0, 0)

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
    match Chord.format mods sym with
    | Some "M-S-q" -> Ffi.wtf_quit (); 1 // host-level: quit the compositor
    | Some chord ->
        match Keymap.lookup cfg chord with
        | Some cmd ->
            let w', effects = Reducer.apply cmd world
            world <- w'
            applyEffects effects
            1
        | None -> 0
    | None -> 0

let onOutputResize (width: int) (height: int) : unit =
    world <- { world with Screen = Rect.create 0 0 width height }
    applyEffects [ Arrange(World.arrange world) ]

// ---- agent-first IPC, marshalled onto the loop thread by the bridge ----
let private bridge = Bridge.LoopBridge()

/// Apply one control-socket request ON the loop thread (safe to mutate world and
/// call wlroots), returning the resulting snapshot. A Query changes nothing.
let private handleOnLoop (line: string) : string =
    match Protocol.parseRequest line with
    | Some Protocol.Query -> Protocol.snapshotLine world
    | Some (Protocol.Act cmd) ->
        let w', effects = Reducer.apply cmd world
        world <- w'
        applyEffects effects
        Protocol.snapshotLine world
    | None -> """{"error":"unrecognized command"}"""

/// Drain callback — the compositor fires this on the loop thread after a notify.
let onDrain () : unit = bridge.Drain handleOnLoop

let onReady () : unit =
    // The compositor is live; open the agent door and launch startup clients so
    // you see tiled windows immediately instead of an empty output.
    let submit line = bridge.Submit(Ffi.wtf_command_notify, line)
    let path = Ipc.start submit
    // Push the configured appearance into the renderer.
    Ffi.wtf_set_inactive_opacity cfg.InactiveOpacity
    Ffi.wtf_set_anim_speed cfg.AnimSpeed
    Ffi.wtf_set_border_width cfg.BorderWidth
    let border active hex =
        match Protocol.hexColor hex with
        | Some (r, g, b) -> Ffi.wtf_set_border_color ((if active then 1 else 0), r, g, b)
        | None -> ()
    border true cfg.ActiveBorder
    border false cfg.InactiveBorder
    Ffi.wtf_set_corner_radius cfg.CornerRadius
    Ffi.wtf_set_blur ((if cfg.Blur then 1 else 0), 0, 0)
    eprintfn "WTF: ready — agent socket at %s — spawning startup: %A" path cfg.StartupApps
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
