namespace WTF.Core

/// A layout is a *pure function*: given the screen area and the focus-aware
/// stack of windows, produce a placement for each one. This is the whole
/// xMonad idea — `layout : Rect -> Stack -> (window * Rect) list` — and it is
/// the contract the C compositor calls across the FFI: F# returns rectangles,
/// C applies them (and later, animates *towards* them).
type Layout<'a> = Rect -> Stack<'a> -> ('a * Rect) list

module Layout =

    /// Fullscreen: every window gets the whole area (focus ends up on top).
    let full: Layout<'a> =
        fun area s -> Stack.toList s |> List.map (fun w -> w, area)

    /// xMonad's "Tall": `nmaster` windows in a master column on the left taking
    /// `ratio` of the width; everyone else stacked in a column on the right.
    let tall (nmaster: int) (ratio: float) : Layout<'a> =
        fun area s ->
            let ws = Stack.toList s
            let n = List.length ws
            let nmaster = max 1 nmaster
            if n = 0 then []
            elif n <= nmaster then
                List.zip ws (Rect.columnOf n area)
            else
                let masters = List.truncate nmaster ws
                let rest = List.skip nmaster ws
                let left, right = Rect.splitVertical ratio area
                List.zip masters (Rect.columnOf masters.Length left)
                @ List.zip rest (Rect.columnOf rest.Length right)

    /// Binary space partition: recursively halve the area, alternating
    /// vertical / horizontal, one window per leaf (Hyprland's default feel).
    let bsp: Layout<'a> =
        fun area s ->
            let rec go vertical area ws =
                match ws with
                | [] -> []
                | [ w ] -> [ w, area ]
                | w :: rest ->
                    let a, b =
                        if vertical then Rect.splitVertical 0.5 area
                        else Rect.splitHorizontal 0.5 area
                    (w, a) :: go (not vertical) b rest
            go true area (Stack.toList s)

    /// (n columns x m rows) grid — xMonad's Grid. Columns = ceil(sqrt n).
    let grid: Layout<'a> =
        fun area s ->
            let ws = Stack.toList s
            let n = List.length ws
            if n = 0 then []
            else
                let cols = int (ceil (sqrt (float n)))
                let rows = int (ceil (float n / float cols))
                let cw = area.Width / cols
                let ch = area.Height / rows
                ws
                |> List.mapi (fun i w ->
                    let col, row = i % cols, i / cols
                    let lastCol = col = cols - 1
                    let lastRow = row = rows - 1
                    w,
                    { X = area.X + col * cw
                      Y = area.Y + row * ch
                      Width = (if lastCol then area.Width - col * cw else cw)
                      Height = (if lastRow then area.Height - row * ch else ch) })

    // --- layout modifiers: take a layout, return a transformed layout ---

    /// Rotate a layout 90° (xMonad's Mirror): a left/right split becomes
    /// top/bottom. Transpose the area, lay out, transpose the results back.
    let mirror (layout: Layout<'a>) : Layout<'a> =
        let t (r: Rect) = { X = r.Y; Y = r.X; Width = r.Height; Height = r.Width }
        fun area s -> layout (t area) s |> List.map (fun (w, r) -> w, t r)

    /// Reflect left<->right within the area. The reflection bounds come from the
    /// runtime `area` the layout is invoked with; the leading `_area0` parameter
    /// is intentionally ignored (kept only for call-site shape / future wiring).
    let reflectHoriz (_area0: Rect) (layout: Layout<'a>) : Layout<'a> =
        fun area s ->
            layout area s
            |> List.map (fun (w, r) ->
                w, { r with X = area.X + (area.X + area.Width - (r.X + r.Width)) })

    /// Apply a uniform gap around every tile produced by a layout.
    let withGaps gap (layout: Layout<'a>) : Layout<'a> =
        fun area s -> layout area s |> List.map (fun (w, r) -> w, Rect.pad gap r)
