module WTF.Bar.Program

// WTF.Bar — the status bar executable. A standalone Wayland-client (layer-shell
// TOP, anchored top full-width with an exclusive zone) that polls the WM agent
// socket for the snapshot, builds the PURE BarModel content model, and renders it
// with ImageSharp into the shm buffer libwtf_panel hands up.
//
// "F# brain, C body": the C helper does ONLY the Wayland/layer-shell/shm
// plumbing; every pixel + every layout decision is here. GRACEFUL throughout — no
// compositor / no socket / no font degrades to whatever can be drawn; a missing
// wl_shm / layer-shell exits cleanly with a logged error.

open System
open System.Threading
open SixLabors.ImageSharp
open WTF.Client

[<Literal>]
let BarHeight = 28

// ---- Catppuccin-ish palette (ARGB8888 == Bgra32 memory, no swap) -------------
let private cBg = Color.FromRgba(30uy, 30uy, 46uy, 235uy)        // base, slightly translucent
let private cText = Color.FromRgba(205uy, 214uy, 244uy, 255uy)   // text
let private cDim = Color.FromRgba(108uy, 112uy, 134uy, 255uy)    // overlay0 (idle ws)
let private cAccent = Color.FromRgba(137uy, 180uy, 250uy, 255uy) // blue (current ws bg)
let private cOnAccent = Color.FromRgba(30uy, 30uy, 46uy, 255uy)  // dark text on accent
let private cOccupied = Color.FromRgba(166uy, 173uy, 200uy, 255uy)
let private cPlayer = Color.FromRgba(166uy, 227uy, 161uy, 255uy) // green (now playing)

// Latest snapshot JSON, refreshed by the poll thread; read by the render callback.
let mutable private latestSnapshot : string = ""
let private surface = new Render.Surface()
let mutable private handle : Panel.Handle option = None

// ---- render the bar content model into the shm buffer ------------------------

/// Right-segment display text (left/workspaces handled separately as pills).
let private rightText (seg: BarModel.Segment) : string =
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

let private render (buf: nativeint) (w: int) (h: int) (stride: int) =
    let model = BarModel.build DateTime.Now latestSnapshot
    let fontOpt = Render.font 14.0f
    surface.Draw(
        w, h,
        fun ctx ->
            Render.fillRect ctx cBg 0.0f 0.0f (float32 w) (float32 h)
            match fontOpt with
            | None -> ()
            | Some font ->
                let padX = 8.0f
                let pillPad = 9.0f
                // vertical text origin (top-left) roughly centered in the bar
                let textY = (float32 h - 14.0f) / 2.0f - 1.0f

                // --- left: workspace pills, laid out left -> right -------------
                let mutable x = padX
                for seg in model.Left do
                    match seg with
                    | BarModel.Workspace(tag, current, occupied) ->
                        let tw = Render.measureWidth font tag
                        let pillW = tw + pillPad * 2.0f
                        if current then
                            Render.fillRoundedRect ctx cAccent x 3.0f pillW (float32 h - 6.0f) 6.0f
                        let fg =
                            if current then cOnAccent
                            elif occupied then cOccupied
                            else cDim
                        Render.drawText ctx font fg (x + pillPad) textY tag
                        x <- x + pillW + 4.0f
                    | other ->
                        let s = rightText other
                        let tw = Render.measureWidth font s
                        Render.drawText ctx font cText x textY s
                        x <- x + tw + 12.0f

                // --- right: segments laid out right -> left -------------------
                let mutable rx = float32 w - padX
                for seg in List.rev model.Right do
                    let s = rightText seg
                    if not (String.IsNullOrEmpty s) then
                        let tw = Render.measureWidth font s
                        let fg =
                            match seg with
                            | BarModel.Player _ -> cPlayer
                            | _ -> cText
                        rx <- rx - tw
                        Render.drawText ctx font fg rx textY s
                        rx <- rx - 16.0f
    )
    surface.Blit(buf, w, h, stride)

let private key (_keysym: uint32) (_codepoint: uint32) = ()   // the bar takes no keyboard

let private configure (_w: int) (_h: int) = ()                // surface auto-resizes on Draw

let private closed () =
    handle |> Option.iter (fun h -> h.Quit())

// ---- entry point -------------------------------------------------------------

[<EntryPoint>]
let main _argv =
    let h = Panel.Handle(render, key, configure, closed)
    handle <- Some h

    let mutable cfg = Panel.Config()
    cfg.Ns <- "wtf-bar"
    cfg.Layer <- Panel.LayerTop
    cfg.Anchor <- Panel.AnchorTop ||| Panel.AnchorLeft ||| Panel.AnchorRight
    cfg.Width <- 0                       // full width (compositor decides)
    cfg.Height <- BarHeight
    cfg.ExclusiveZone <- BarHeight       // reserve our strip
    cfg.Keyboard <- Panel.KeyboardNone
    cfg.MarginTop <- 0
    cfg.MarginRight <- 0
    cfg.MarginBottom <- 0
    cfg.MarginLeft <- 0

    // Prime the first snapshot before the surface comes up so the bar paints
    // real content on its very first frame (degrades to clock-only if no WM).
    latestSnapshot <- defaultArg (Socket.trySend "state") ""

    match h.Init cfg with
    | rc when rc < 0 ->
        eprintfn "wtf-bar: cannot start (no Wayland display / missing wl_shm / missing layer-shell). rc=%d" rc
        2
    | _ ->
        // Poll the WM for a fresh snapshot on a timer; redraw on every tick so the
        // clock advances even when the WM state is unchanged. Background thread so
        // the wl dispatch loop owns the main thread.
        let poll =
            Thread(fun () ->
                while true do
                    (match Socket.trySend "state" with
                     | Some s -> latestSnapshot <- s
                     | None -> ())
                    h.RequestRedraw()
                    Thread.Sleep 1000)
        poll.IsBackground <- true
        poll.Start()

        let rc = h.Run()
        (surface :> IDisposable).Dispose()
        if rc < 0 then 1 else 0
