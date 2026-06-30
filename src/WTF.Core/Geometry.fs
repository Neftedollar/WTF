namespace WTF.Core

/// Units of measure for the two coordinate spaces WTF deals in. The brain works
/// entirely in *logical* pixels; *physical* (device) pixels exist only as a
/// transient at the HiDPI scaling boundary. Tagging `Rect` with `lpx` makes the
/// FFI strip explicit: a plain `int` parameter (wtf_configure) won't accept an
/// `int<lpx>`, so forgetting to convert is a compile error, not a runtime bug.
[<Measure>] type lpx   // logical pixels — WTF's brain coordinate space
[<Measure>] type ppx   // physical/device pixels — post-scale, hardware

/// Integer screen rectangle. Wayland surface geometry is integer pixels,
/// so the whole layout engine works in ints — no float drift across the FFI.
type Rect =
    { X: int<lpx>; Y: int<lpx>; Width: int<lpx>; Height: int<lpx> }

module Rect =
    /// Smart constructor. Inputs are plain ints — a Rect is *by definition* in
    /// logical pixels, so this is the one place raw ints become `lpx`. Keeping
    /// the input plain leaves every existing call site (and the test suite)
    /// textually unchanged while the fields below carry the measure downstream.
    let create (x: int) (y: int) (w: int) (h: int) : Rect =
        { X = x * 1<lpx>; Y = y * 1<lpx>; Width = w * 1<lpx>; Height = h * 1<lpx> }

    let area r = r.Width * r.Height

    /// Split into (left, right) at `ratio` of the width (0.0..1.0).
    /// The two halves exactly tile the original — no gap, no overlap.
    /// `ratio` is clamped to [0,1] so a wild value (e.g. from a hand-edited
    /// session/config) degrades to a zero-width half instead of an inverted,
    /// negative-width rect; exactness (lw + (W-lw) = W) is preserved either way.
    let splitVertical (ratio: float) r =
        let lw = max 0<lpx> (min r.Width (int (float r.Width * ratio) * 1<lpx>))
        { r with Width = lw },
        { r with X = r.X + lw; Width = r.Width - lw }

    /// Split into (top, bottom) at `ratio` of the height. `ratio` clamped to
    /// [0,1] (see splitVertical) so neither half can become negative-height.
    let splitHorizontal (ratio: float) r =
        let th = max 0<lpx> (min r.Height (int (float r.Height * ratio) * 1<lpx>))
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
    /// The gap is clamped per-axis to [0, dim/2] so an oversized gap (Gaps is an
    /// unbounded int reachable from config / `incgaps` / a hand-edited session)
    /// degrades to a zero-size tile instead of producing an inverted, negative-
    /// dimension rect. A negative gap is clamped to 0 (a no-op), never an expand.
    let pad (gap: int<lpx>) r =
        let clamp (dim: int<lpx>) = max 0<lpx> (min gap (dim / 2))
        let gx, gy = clamp r.Width, clamp r.Height
        { X = r.X + gx; Y = r.Y + gy
          Width = r.Width - 2 * gx; Height = r.Height - 2 * gy }

/// Strip / convert measures at the HiDPI boundary. The brain never needs a
/// `Rect<ppx>`; physical pixels appear only as transient `int<ppx>` here.
module Px =
    let inline rawL (v: int<lpx>) : int = int v
    let inline rawP (v: int<ppx>) : int = int v

    /// logical -> physical at `scale` (e.g. 2.0 on a HiDPI output).
    let toPhysical (scale: float) (v: int<lpx>) : int<ppx> =
        int (round (float (int v) * scale)) * 1<ppx>

    /// physical -> logical (compositor reports device px on resize).
    let toLogical (scale: float) (v: int<ppx>) : int<lpx> =
        int (round (float (int v) / scale)) * 1<lpx>

module Scaling =
    /// A logical tile -> the plain ints `wtf_configure` wants.
    /// scale = 1.0 is an identity strip: values (and the JSON wire format)
    /// are byte-for-byte unchanged. For scale <> 1.0 we scale the *edges* and
    /// subtract, never the sizes directly, so adjacent tiles still abut exactly
    /// with no 1px HiDPI gap/overlap.
    let configure (scale: float) (r: Rect) : int * int * int * int =
        if scale = 1.0 then int r.X, int r.Y, int r.Width, int r.Height
        else
            let e (a: int<lpx>) = Px.rawP (Px.toPhysical scale a)
            let x, y = e r.X, e r.Y
            x, y, e (r.X + r.Width) - x, e (r.Y + r.Height) - y
