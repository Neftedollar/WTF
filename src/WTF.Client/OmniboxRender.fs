namespace WTF.Client

// The omnibox pixel composition, factored out of the standalone `wtf-omnibox`
// exe so the in-process overlay (the compositor host) renders through the EXACT
// same path — one launcher look, two shells. Pure given (ui, model, w, h): it
// draws the input row + ranked results into a `Render.Surface`. Mirrors
// `BarRender` for the bar.

open SixLabors.ImageSharp
open WTF.Client.ClientConfig

module OmniboxRender =

    /// The fixed input-row height (px) at the top of the panel.
    [<Literal>]
    let InputHeight = 40

    /// How many result rows fit under the input row for this ui height.
    let rows (ui: OmniboxUi) : int = max 1 ((ui.Height - InputHeight) / ui.RowHeight)

    /// Draw the omnibox for `model` into `surface` at `w`x`h`. The caller blits /
    /// copies the surface out afterwards (Blit for the exe, CopyOut for the host).
    let draw (surface: Render.Surface) (ui: OmniboxUi) (model: OmniboxModel.Model) (w: int) (h: int) =
        let fontOpt = Render.font ui.FontSize
        let smallOpt = Render.font (max 8.0f (ui.FontSize * 0.75f))
        let cOnSel = ui.Bg.WithAlpha 1.0f      // dark text on the selection color
        let maxRows = rows ui
        surface.Draw(
            w, h,
            fun ctx ->
                Render.fillRect ctx ui.Bg 0.0f 0.0f (float32 w) (float32 h)
                match fontOpt with
                | None -> ()
                | Some font ->
                    let padX = 14.0f
                    // --- input row ------------------------------------------------
                    Render.fillRect ctx ui.InputBg 0.0f 0.0f (float32 w) (float32 InputHeight)
                    let inY = (float32 InputHeight - ui.FontSize) / 2.0f - 1.0f
                    Render.drawText ctx font ui.PromptColor padX inY ui.Prompt
                    let promptW = Render.measureWidth font (ui.Prompt + " ")
                    let shown = model.Query
                    Render.drawText ctx font ui.Fg (padX + promptW) inY shown
                    // a simple caret after the query text
                    let qW = Render.measureWidth font shown
                    Render.fillRect ctx ui.Fg (padX + promptW + qW + 1.0f) (inY + 1.0f) 2.0f 18.0f
                    if model.Query = "" then
                        Render.drawText ctx font ui.Dim (padX + promptW + 12.0f) inY ui.Placeholder

                    // --- results list ---------------------------------------------
                    let ranked = model.Ranked
                    let selected = model.Selected
                    let count = min maxRows ranked.Length
                    // scroll the window so the selected row stays visible
                    let first = if selected < count then 0 else selected - count + 1
                    for i in 0 .. count - 1 do
                        let idx = first + i
                        if idx < ranked.Length then
                            let e = ranked.[idx]
                            let top = float32 (InputHeight + i * ui.RowHeight)
                            let isSel = (idx = selected)
                            if isSel then
                                Render.fillRect ctx ui.Selection 0.0f top (float32 w) (float32 ui.RowHeight)
                            let fg = if isSel then cOnSel else ui.Fg
                            let ty = top + (float32 ui.RowHeight - ui.FontSize) / 2.0f - 1.0f
                            Render.drawText ctx font fg padX ty e.Name
                            // right-aligned dim command hint
                            match smallOpt with
                            | Some small ->
                                let hint = DesktopEntry.stripFieldCodes e.Exec
                                let hint = if hint.Length > 40 then hint.Substring(0, 39) + "…" else hint
                                let hw = Render.measureWidth small hint
                                let hintFg = if isSel then cOnSel else ui.Dim
                                Render.drawText ctx small hintFg (float32 w - padX - hw) (top + (float32 ui.RowHeight - 12.0f) / 2.0f - 1.0f) hint
                            | None -> ())
