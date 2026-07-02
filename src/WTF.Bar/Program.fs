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

// On-accent text: the darkest of bg-vs-fg gives contrast on the accent pill
// without a dedicated knob (bg is the right pick for every dark theme).
let private onAccent (u: BarUi) = u.Bg.WithAlpha 1.0f

/// Display text for a non-workspace segment.
let private segText (seg: BarModel.Segment) : string =
    match seg with
    | BarModel.Clock s -> s
    | BarModel.Battery(pct, state) ->
        let glyph =
            match state.ToLowerInvariant() with
            | "charging" -> "+"
            | "fully-charged" | "full" -> "="
            | _ -> ""
        sprintf "BAT %d%%%s" pct glyph
    | BarModel.Network s -> sprintf "NET %s" s
    | BarModel.Player(_, title, artist) ->
        let t = if String.IsNullOrWhiteSpace title then "" else title
        let a = if String.IsNullOrWhiteSpace artist then "" else artist
        match a, t with
        | "", "" -> "Playing"
        | "", t -> sprintf "▶ %s" t
        | a, "" -> sprintf "▶ %s" a
        | a, t -> sprintf "▶ %s - %s" a t
    | BarModel.Text s -> s
    | BarModel.Workspace(tag, _, _) -> tag

/// Short text for VERTICAL bars (narrow strip: no room for long strings).
let private segTextVertical (seg: BarModel.Segment) : string =
    match seg with
    | BarModel.Battery(pct, _) -> sprintf "%d" pct
    | BarModel.Network _ -> "NET"
    | BarModel.Player _ -> "▶"
    | other -> segText other

let private renderHorizontal (u: BarUi) (model: BarModel.BarModel) ctx (w: int) (h: int) font =
    let padX = 8.0f
    let pillPad = 9.0f
    let fs = u.FontSize
    // vertical text origin (top-left) roughly centered in the bar
    let textY = (float32 h - fs) / 2.0f - 1.0f

    // --- left segments, laid out left -> right --------------------------------
    let mutable x = padX
    for seg in model.Left do
        match seg with
        | BarModel.Workspace(tag, current, occupied) ->
            let tw = Render.measureWidth font tag
            let pillW = tw + pillPad * 2.0f
            if current then
                Render.fillRoundedRect ctx u.Accent x 3.0f pillW (float32 h - 6.0f) 6.0f
            let fg =
                if current then onAccent u
                elif occupied then u.Fg
                else u.Dim
            Render.drawText ctx font fg (x + pillPad) textY tag
            x <- x + pillW + 4.0f
        | other ->
            let s = segText other
            let tw = Render.measureWidth font s
            let fg = match other with BarModel.Player _ -> u.Accent | _ -> u.Fg
            Render.drawText ctx font fg x textY s
            x <- x + tw + 12.0f

    // --- right segments, laid out right -> left -------------------------------
    let mutable rx = float32 w - padX
    for seg in List.rev model.Right do
        let s = segText seg
        if not (String.IsNullOrEmpty s) then
            let tw = Render.measureWidth font s
            let fg = match seg with BarModel.Player _ -> u.Accent | _ -> u.Fg
            rx <- rx - tw
            Render.drawText ctx font fg rx textY s
            rx <- rx - 16.0f

let private renderVertical (u: BarUi) (model: BarModel.BarModel) ctx (w: int) (h: int) font =
    let rowH = u.FontSize + 10.0f
    let centerX (s: string) = max 2.0f ((float32 w - Render.measureWidth font s) / 2.0f)

    // --- Left list stacks from the TOP ----------------------------------------
    let mutable y = 6.0f
    for seg in model.Left do
        match seg with
        | BarModel.Workspace(tag, current, occupied) ->
            if current then
                Render.fillRoundedRect ctx u.Accent 3.0f y (float32 w - 6.0f) rowH 6.0f
            let fg =
                if current then onAccent u
                elif occupied then u.Fg
                else u.Dim
            Render.drawText ctx font fg (centerX tag) (y + 5.0f) tag
            y <- y + rowH + 2.0f
        | other ->
            let s = segTextVertical other
            Render.drawText ctx font u.Fg (centerX s) (y + 5.0f) s
            y <- y + rowH + 2.0f

    // --- Right list stacks from the BOTTOM ------------------------------------
    let mutable by = float32 h - rowH - 6.0f
    for seg in List.rev model.Right do
        match seg with
        | BarModel.Clock text ->
            // "23:18" won't fit a narrow strip horizontally: split on ':' into
            // stacked lines (HH over mm); other separators render as one line.
            let parts = text.Split(':')
            let mutable py = by - float32 (parts.Length - 1) * (u.FontSize + 2.0f)
            for part in parts do
                Render.drawText ctx font u.Fg (centerX part) py part
                py <- py + u.FontSize + 2.0f
            by <- by - float32 parts.Length * (u.FontSize + 2.0f) - 8.0f
        | other ->
            let s = segTextVertical other
            let fg = match other with BarModel.Player _ -> u.Accent | _ -> u.Fg
            Render.drawText ctx font fg (centerX s) by s
            by <- by - rowH

let private render (buf: nativeint) (w: int) (h: int) (stride: int) =
    let u = ui
    let model = BarModel.buildWith u.Left u.Right DateTime.Now latestSnapshot
    let fontOpt = Render.font u.FontSize
    surface.Draw(
        w, h,
        fun ctx ->
            Render.fillRect ctx u.Bg 0.0f 0.0f (float32 w) (float32 h)
            match fontOpt with
            | None -> ()
            | Some font ->
                match u.Side with
                | SideLeft | SideRight -> renderVertical u model ctx w h font
                | _ -> renderHorizontal u model ctx w h font
    )
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
        // Poll the WM for a fresh snapshot + LIVE styling on a timer; redraw on
        // every tick so the clock advances even when the WM state is unchanged.
        // Background thread so the wl dispatch loop owns the main thread.
        let poll =
            Thread(fun () ->
                while true do
                    (match Socket.trySend "state" with
                     | Some s ->
                         latestSnapshot <- s
                         ui <- { barOfSnapshot barName s with Side = ui.Side; Height = ui.Height }
                     | None -> ())
                    h.RequestRedraw()
                    Thread.Sleep 1000)
        poll.IsBackground <- true
        poll.Start()

        let rc = h.Run()
        (surface :> IDisposable).Dispose()
        if rc < 0 then 1 else 0
