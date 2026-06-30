namespace WTF.Core

open WTF.Core.Color

/// A GENERATIVE, structured palette — the user-facing color type for ricing
/// configs. A palette is NOT a `Color list` (that would describe every pixel:
/// expensive + meaningless indices). It is like a vector image: a few anchors +
/// a RULE that generates colors, exposed by SEMANTIC ROLE, with a continuous
/// RAMP (a curve) as the primitive. The raw color list is an internal extraction
/// detail (see `ofColors`), never the user-facing shape. Pure, no IO.
module Palette =

    /// THE VECTOR PRIMITIVE: a ramp is a CURVE sampled at t in [0,1] — colors are
    /// GENERATED on demand, not stored. Everything user-facing is built from this.
    type Ramp = float -> Color

    let inline private clamp01 (x: float) = if x < 0.0 then 0.0 elif x > 1.0 then 1.0 else x

    /// Builders + samplers for the ramp primitive.
    [<RequireQualifiedAccess>]
    module Ramp =

        /// A constant ramp (same color for every t).
        let constant (c: Color) : Ramp = fun _ -> c

        /// Piecewise OKLab interpolation between sorted stops (the "few anchors + a
        /// rule" generator). t is clamped to [0,1]. Single stop = constant; empty =
        /// constant default Text-ish gray.
        let ofStops (stops: (float * Color) list) : Ramp =
            match List.sortBy fst stops with
            | [] -> constant { R = 0.8; G = 0.8; B = 0.85; A = 1.0 }
            | [ (_, c) ] -> constant c
            | sorted ->
                fun t ->
                    let t = clamp01 t
                    let first = List.head sorted
                    let last = List.last sorted
                    if t <= fst first then snd first
                    elif t >= fst last then snd last
                    else
                        // find the bracketing pair
                        let rec go =
                            function
                            | (t0, c0) :: ((t1, c1) :: _ as rest) ->
                                if t <= t1 then
                                    let local = if t1 - t0 < 1e-12 then 0.0 else (t - t0) / (t1 - t0)
                                    Color.mix local c0 c1
                                else go rest
                            | [ (_, c) ] -> c
                            | [] -> snd last
                        go sorted

        /// The literal parametric hue-sweep curve at fixed L/C: hue runs from
        /// `hue0` to `hue0 + hueSpan` across t in [0,1].
        let oklchSweep (l: float) (c: float) (hue0: float) (hueSpan: float) : Ramp =
            fun t -> Color.ofOklch { L = l; C = c; H = hue0 + clamp01 t * hueSpan }

        /// Post-compose a per-color transform onto a ramp.
        let map (f: Color -> Color) (r: Ramp) : Ramp = r >> f

        /// Sample a ramp at t.
        let sample (t: float) (r: Ramp) : Color = r t

        /// n evenly-spaced samples, endpoints inclusive (`samples 1 = [r 0]`).
        let samples (n: int) (r: Ramp) : Color list =
            if n <= 0 then []
            elif n = 1 then [ r 0.0 ]
            else [ for i in 0 .. n - 1 -> r (float i / float (n - 1)) ]

    /// SEMANTIC ROLES — what config references (MEANING, not index). `Accents` is a
    /// RAMP you sample (`accent t p`), not a fixed bag of colors.
    type Palette =
        { Base: Color
          Surface: Color
          Overlay: Color
          Text: Color
          Subtext: Color
          Accents: Ramp }

    /// BUILT-IN DEFAULT: a Catppuccin-Mocha-ish dark. Used whenever no wallpaper
    /// palette is available, so every config referencing the palette resolves. Also
    /// the value `RenderContext.Palette` defaults to.
    let defaultPalette : Palette =
        let hx s = Color.ofHexOr Color.black s
        let baseC = hx "#1e1e2e" // matches the default solid wallpaper
        { Base = baseC
          Surface = hx "#313244"
          Overlay = hx "#45475a"
          Text = hx "#cdd6f4"
          Subtext = hx "#a6adc8"
          // blue (#89b4fa) -> mauve (#cba6f7) sweep in OKLCH
          Accents =
            let blue = Color.toOklch (hx "#89b4fa")
            let mauve = Color.toOklch (hx "#cba6f7")
            Ramp.oklchSweep blue.L blue.C blue.H (mauve.H - blue.H) }

    // =====================================================================
    // GENERATIVE CONSTRUCTORS.
    // =====================================================================

    /// ONE anchor + rules -> a full palette. Base = the seed brought to a low L /
    /// low C; Surface/Overlay = lightness steps; Text = readable on Base; Subtext =
    /// Text pulled toward Base; Accents = a hue sweep CENTERED on the seed hue.
    /// "One number (the seed) + a curve generates many colors."
    let ofSeed (seed: Oklch) : Palette =
        let baseC = Color.ofOklch { L = 0.18; C = min seed.C 0.04; H = seed.H }
        let text = Color.readableOn baseC
        { Base = baseC
          Surface = Color.lighten 0.06 baseC
          Overlay = Color.lighten 0.12 baseC
          Text = text
          Subtext = Color.mix 0.35 text baseC
          Accents = Ramp.oklchSweep 0.72 (max 0.08 seed.C) (seed.H - 30.0) 60.0 }

    /// Assign a RAW dominant list to ROLES (the list is INTERNAL here — never
    /// escapes). Order-independent (argmin/argmax), so determinism holds for any
    /// input order. Empty -> `defaultPalette`.
    let ofColors (cs: Color list) : Palette =
        match cs with
        | [] -> defaultPalette
        | _ ->
            let baseC = cs |> List.minBy Color.relativeLuminance
            let text = Color.readableFrom cs baseC
            let anchor = cs |> List.maxBy (fun c -> (Color.toOklch c).C)
            let aOk = Color.toOklch anchor
            { Base = baseC
              Surface = Color.lighten 0.06 baseC
              Overlay = Color.lighten 0.12 baseC
              Text = text
              Subtext = Color.mix 0.35 text baseC
              Accents = Ramp.oklchSweep aOk.L aOk.C (aOk.H - 30.0) 60.0 }

    // =====================================================================
    // ALGEBRA — palettes transform + compose like vector ops.
    // =====================================================================

    /// Apply `f` to every role color AND post-compose it onto the ramp.
    let map (f: Color -> Color) (p: Palette) : Palette =
        { Base = f p.Base
          Surface = f p.Surface
          Overlay = f p.Overlay
          Text = f p.Text
          Subtext = f p.Subtext
          Accents = p.Accents >> f }

    let darken (amt: float) (p: Palette) : Palette = map (Color.darken amt) p
    let lighten (amt: float) (p: Palette) : Palette = map (Color.lighten amt) p
    let shiftHue (deg: float) (p: Palette) : Palette = map (Color.rotateHue deg) p

    /// Re-establish legibility after a transform: recompute Text/Subtext as
    /// readable on the (possibly changed) Base.
    let withContrast (p: Palette) : Palette =
        let text = Color.readableOn p.Base
        { p with Text = text; Subtext = Color.mix 0.35 text p.Base }

    /// Role-wise perceptual blend at t (ramp blended pointwise too).
    let blend (t: float) (a: Palette) (b: Palette) : Palette =
        { Base = Color.mix t a.Base b.Base
          Surface = Color.mix t a.Surface b.Surface
          Overlay = Color.mix t a.Overlay b.Overlay
          Text = Color.mix t a.Text b.Text
          Subtext = Color.mix t a.Subtext b.Subtext
          Accents = fun u -> Color.mix t (a.Accents u) (b.Accents u) }

    /// Sample the accent ramp at t.
    let accent (t: float) (p: Palette) : Color = p.Accents t

    /// Snap `c` to the nearest ROLE color (perceptual deltaE).
    let nearestRole (c: Color) (p: Palette) : Color =
        Color.nearest [ p.Base; p.Surface; p.Overlay; p.Text; p.Subtext ] c
