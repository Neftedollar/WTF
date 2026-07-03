module WTF.Bar.Program

// WTF.Bar — the status bar executable. A standalone Wayland-client (layer-shell
// TOP, anchored to a screen edge with an exclusive zone) that polls the WM agent
// socket for the snapshot, builds the PURE BarModel content model, and renders it
// with ImageSharp into the shm buffer libwtf_panel hands up.
//
// CONFIGURED FROM config.fsx: the WM serves BarConfig under "ui.bars" in the
// snapshot. Colors / segments / font restyle LIVE (each poll re-reads them);
// geometry (position/thickness) is applied at startup — restart the bar to move
// it. Multiple bars: one process per config entry, selected with --name <name>
// (no flag = the first entry). Left/Right positions render VERTICALLY: segments
// stack top->bottom (Left list) and bottom->top (Right list).
//
// "F# brain, C body": the C helper does ONLY the Wayland/layer-shell/shm
// plumbing; every pixel + every layout decision is here. GRACEFUL throughout — no
// compositor / no socket / no ui config degrades to the built-in look.

open System
open System.Threading
open SixLabors.ImageSharp
open WTF.Client
open WTF.Client.ClientConfig

// Latest snapshot JSON, refreshed by the poll thread; read by the render callback.
let mutable private latestSnapshot : string = ""
// The live bar styling (colors/segments/font re-read every poll; Side/Height
// only honored at startup). Written by the poll thread, read by render — a
// stale read costs one frame of the old style, harmless.
let mutable private ui : BarUi = barDefaults
let mutable private barName : string option = None
let private surface = new Render.Surface()
let mutable private handle : Panel.Handle option = None

// The bar render composition now lives in WTF.Client.BarRender (shared with the
// in-process embedded bar in the compositor host). This client shell only feeds
// it the live model + styling and blits the result to the shm buffer.
let private render (buf: nativeint) (w: int) (h: int) (stride: int) =
    let u = ui
    let model = BarModel.buildWith u.Left u.Right DateTime.Now latestSnapshot
    BarRender.draw surface u model w h
    surface.Blit(buf, w, h, stride)

let private key (_keysym: uint32) (_codepoint: uint32) = ()   // the bar takes no keyboard

let private configure (_w: int) (_h: int) = ()                // surface auto-resizes on Draw

let private closed () =
    handle |> Option.iter (fun h -> h.Quit())

// ---- entry point -------------------------------------------------------------

[<EntryPoint>]
let main argv =
    // `wtf-bar --name <name>` binds this process to the config entry with that
    // Name; no flag = the first/only entry.
    barName <-
        match argv |> Array.tryFindIndex (fun a -> a = "--name") with
        | Some i when i + 1 < argv.Length -> Some argv.[i + 1]
        | _ -> None

    let h = Panel.Handle(render, key, configure, closed)
    handle <- Some h

    // Prime the first snapshot BEFORE Init so geometry (position/thickness)
    // comes from the user's config on the very first map. Degrades to the
    // built-in top bar when there is no WM/socket/ui yet.
    latestSnapshot <- defaultArg (Socket.trySend "state") ""
    ui <- barOfSnapshot barName latestSnapshot

    let mutable cfg = Panel.Config()
    cfg.Ns <- "wtf-bar"
    cfg.Layer <- Panel.LayerTop
    cfg.Keyboard <- Panel.KeyboardNone
    cfg.MarginTop <- 0
    cfg.MarginRight <- 0
    cfg.MarginBottom <- 0
    cfg.MarginLeft <- 0
    (match ui.Side with
     | SideTop ->
         cfg.Anchor <- Panel.AnchorTop ||| Panel.AnchorLeft ||| Panel.AnchorRight
         cfg.Width <- 0                       // full width (compositor decides)
         cfg.Height <- ui.Height
     | SideBottom ->
         cfg.Anchor <- Panel.AnchorBottom ||| Panel.AnchorLeft ||| Panel.AnchorRight
         cfg.Width <- 0
         cfg.Height <- ui.Height
     | SideLeft ->
         cfg.Anchor <- Panel.AnchorLeft ||| Panel.AnchorTop ||| Panel.AnchorBottom
         cfg.Width <- ui.Height               // Height knob = thickness
         cfg.Height <- 0                      // full height
     | SideRight ->
         cfg.Anchor <- Panel.AnchorRight ||| Panel.AnchorTop ||| Panel.AnchorBottom
         cfg.Width <- ui.Height
         cfg.Height <- 0)
    cfg.ExclusiveZone <- ui.Height            // reserve our strip on our edge

    match h.Init cfg with
    | rc when rc < 0 ->
        eprintfn "wtf-bar: cannot start (no Wayland display / missing wl_shm / missing layer-shell). rc=%d" rc
        2
    | _ ->
        // Poll the WM for a fresh snapshot + LIVE styling on a timer (cadence from
        // ui.RefreshMs, configured per bar). Redraw ONLY when the visible content
        // actually changed — the built model (segments + formatted clock) plus the
        // live styling. So a fast poll stays cheap: we notice a change within
        // RefreshMs but only pay an ImageSharp repaint when something differs
        // (the clock digit rolling over is the sole idle redraw, e.g. once/min for
        // "HH:mm"). Background thread so the wl dispatch loop owns the main thread.
        let mutable lastKey = ""
        let poll =
            Thread(fun () ->
                while true do
                    (match Socket.trySend "state" with
                     | Some s ->
                         latestSnapshot <- s
                         ui <- { barOfSnapshot barName s with Side = ui.Side; Height = ui.Height }
                     | None -> ())
                    let u = ui
                    let key =
                        sprintf "%A" (u, BarModel.buildWith u.Left u.Right DateTime.Now latestSnapshot)
                    if key <> lastKey then
                        lastKey <- key
                        h.RequestRedraw()
                    Thread.Sleep(max 50 u.RefreshMs))
        poll.IsBackground <- true
        poll.Start()

        let rc = h.Run()
        (surface :> IDisposable).Dispose()
        if rc < 0 then 1 else 0
