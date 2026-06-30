namespace WTF.Core

/// Integer screen rectangle. Wayland surface geometry is integer pixels,
/// so the whole layout engine works in ints — no float drift across the FFI.
type Rect =
    { X: int; Y: int; Width: int; Height: int }

module Rect =
    let create x y w h = { X = x; Y = y; Width = w; Height = h }

    let area r = r.Width * r.Height

    /// Split into (left, right) at `ratio` of the width (0.0..1.0).
    /// The two halves exactly tile the original — no gap, no overlap.
    let splitVertical (ratio: float) r =
        let lw = int (float r.Width * ratio)
        { r with Width = lw },
        { r with X = r.X + lw; Width = r.Width - lw }

    /// Split into (top, bottom) at `ratio` of the height.
    let splitHorizontal (ratio: float) r =
        let th = int (float r.Height * ratio)
        { r with Height = th },
        { r with Y = r.Y + th; Height = r.Height - th }

    /// Slice into n equal-height rows stacked top-to-bottom.
    /// The last row absorbs the rounding remainder so the column fills r exactly.
    let columnOf n r =
        if n <= 0 then []
        else
            let h = r.Height / n
            [ for i in 0 .. n - 1 ->
                let height = if i = n - 1 then r.Height - (n - 1) * h else h
                { r with Y = r.Y + i * h; Height = height } ]

    /// Shrink uniformly on every side (window gaps / "useless gaps", Hyprland-style).
    let pad gap r =
        { X = r.X + gap; Y = r.Y + gap
          Width = r.Width - 2 * gap; Height = r.Height - 2 * gap }
