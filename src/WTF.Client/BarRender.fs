namespace WTF.Client

open System
open WTF.Client.ClientConfig

/// The bar RENDER COMPOSITION — turns a `BarUi` + `BarModel` into pixels on a
/// `Render.Surface`. Transport-agnostic: no Wayland, no socket, no module state.
/// Shared by BOTH bar shells:
///   * the standalone `wtf-bar` client — draws, then `Surface.Blit`s to the shm
///     buffer libwtf_panel handed up;
///   * the in-process embedded bar in the compositor host — draws, then
///     `Surface.CopyOut`s the pixels into a scene buffer (`wtf_set_bar`).
/// Previously this lived inside WTF.Bar/Program.fs; it moved here so the host can
/// reuse it verbatim.
module BarRender =

    // On-accent text: bg at full alpha reads as contrast on the accent pill
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

    /// Draw `model` styled by `u` into `surface` at w x h. The caller exports the
    /// pixels afterwards: `surface.Blit ptr` (shm) or `surface.CopyOut(w, h)`
    /// (in-process scene buffer). Best-effort — `Surface.Draw` swallows failures.
    let draw (surface: Render.Surface) (u: BarUi) (model: BarModel.BarModel) (w: int) (h: int) : unit =
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
                    | _ -> renderHorizontal u model ctx w h font)
