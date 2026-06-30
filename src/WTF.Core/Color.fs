namespace WTF.Core

open System

/// A real, perceptual color algebra for ricing configs. Pure F#, no IO, AOT-safe
/// (plain float math, no reflection). Channels are 0..1 floats. The right space
/// to *generate* and *measure* colors in is OKLab/OKLCH (Bjorn Ottosson), so the
/// flagship operations — `mix`, `deltaE`, `nearest`, the harmonies — live there,
/// NOT in naive sRGB. Hex parsing is SHARED with `Protocol.hexColor` (not
/// reimplemented) so the E1 "bad hex falls back" contract stays one codepath.
module Color =

    /// An sRGB color, channels 0..1 (gamma-encoded), with straight alpha.
    type Color = { R: float; G: float; B: float; A: float }

    /// OKLab: perceptual lightness L (0..1) + opponent axes a (green/red),
    /// b (blue/yellow). Euclidean distance here is ~perceptual (the deltaE space).
    type Oklab = { L: float; a: float; b: float }

    /// OKLCH: cylindrical OKLab. L lightness, C chroma (>=0), H hue in DEGREES
    /// [0,360). The natural space to *generate* colors: tweak one of L/C/H.
    type Oklch = { L: float; C: float; H: float }

    /// HSL convenience space. H in DEGREES [0,360), S/L in [0,1].
    type Hsl = { H: float; S: float; L: float }

    /// HSV convenience space. H in DEGREES [0,360), S/V in [0,1].
    type Hsv = { H: float; S: float; V: float }

    // --- small helpers ---

    let inline private clamp01 (x: float) = if x < 0.0 then 0.0 elif x > 1.0 then 1.0 else x

    /// Sign-preserving cube root, so a slightly-negative linear value (after a
    /// gamut clamp) cube-roots to a real number instead of NaN.
    let inline private cbrt (x: float) = (if x < 0.0 then -1.0 else 1.0) * (abs x) ** (1.0 / 3.0)

    /// Normalize a hue to [0,360).
    let private normHue (h: float) = let m = h % 360.0 in if m < 0.0 then m + 360.0 else m

    // =====================================================================
    // HEX BRIDGE — share Protocol.hexColor; do NOT reimplement 3/6 parsing.
    // =====================================================================

    /// Bridge a Protocol-style (r,g,b) tuple (alpha = 1).
    let ofRgbTuple (r: float, g: float, b: float) : Color = { R = r; G = g; B = b; A = 1.0 }

    /// Drop alpha to the (r,g,b) tuple used by the E1 borderColor string path.
    let toRgbTuple (c: Color) : float * float * float = (c.R, c.G, c.B)

    /// Parse "#rgb" / "#rrggbb" / "#rrggbbaa" (leading '#' optional, at most one)
    /// into a Color. 3/6 forms delegate to `Protocol.hexColor`; 8 form takes the
    /// first 6 as rgb + the last 2 as alpha/255. None on anything Protocol rejects
    /// (preserves the E1 "bad hex falls back" contract).
    let ofHex (s: string) : Color option =
        let h = if not (isNull s) && s.StartsWith "#" then s.Substring 1 else s
        match h with
        | null -> None
        // 8 residual chars after the single strip = #rrggbbaa: split rgb + alpha.
        | _ when h.Length = 8 ->
            match Protocol.hexColor ("#" + h.Substring(0, 6)) with
            | Some(r, g, b) ->
                try
                    let a = float (Convert.ToInt32(h.Substring(6, 2), 16)) / 255.0
                    Some { R = r; G = g; B = b; A = a }
                with _ -> None
            | None -> None
        // 3/6 forms: delegate the ORIGINAL string so Protocol does the (single)
        // '#' strip itself — "##fff" stays rejected (no double-strip).
        | _ -> Protocol.hexColor s |> Option.map ofRgbTuple

    /// `ofHex` with a fallback — the convenience used by the borderColor string path.
    let ofHexOr (fallback: Color) (s: string) : Color = ofHex s |> Option.defaultValue fallback

    /// Emit `#rrggbb` (A>=1) or `#rrggbbaa` (A<1). Channels are clamped to [0,1]
    /// first, so output is ALWAYS valid sRGB. Round-trips with `ofHex` at 8-bit.
    let toHex (c: Color) : string =
        let byte01 (x: float) =
            int (Math.Round(clamp01 x * 255.0, MidpointRounding.AwayFromZero))
        let r, g, b = byte01 c.R, byte01 c.G, byte01 c.B
        if clamp01 c.A >= 1.0 - 1e-9 then sprintf "#%02x%02x%02x" r g b
        else sprintf "#%02x%02x%02x%02x" r g b (byte01 c.A)

    // =====================================================================
    // COLOR SPACES — Bjorn Ottosson OKLab, standard constants.
    // =====================================================================

    let private linearize (c: float) =
        if c <= 0.04045 then c / 12.92 else ((c + 0.055) / 1.055) ** 2.4

    let private delinearize (c: float) =
        if c <= 0.0031308 then c * 12.92 else 1.055 * (c ** (1.0 / 2.4)) - 0.055

    /// Linear-light (r,g,b) of a Color (alpha untouched). Mostly for WCAG luminance.
    let private toLinearRgb (c: Color) = (linearize c.R, linearize c.G, linearize c.B)

    /// sRGB Color -> OKLab.
    let toOklab (c: Color) : Oklab =
        let r, g, b = toLinearRgb c
        let l = 0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b
        let m = 0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b
        let s = 0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b
        let l' = cbrt l
        let m' = cbrt m
        let s' = cbrt s
        { L = 0.2104542553 * l' + 0.7936177850 * m' - 0.0040720468 * s'
          a = 1.9779984951 * l' - 2.4285922050 * m' + 0.4505937099 * s'
          b = 0.0259040371 * l' + 0.7827717662 * m' - 0.8086757660 * s' }

    /// OKLab -> sRGB Color, carrying an explicit alpha through.
    let ofOklabA (alpha: float) (o: Oklab) : Color =
        let l' = o.L + 0.3963377774 * o.a + 0.2158037573 * o.b
        let m' = o.L - 0.1055613458 * o.a - 0.0638541728 * o.b
        let s' = o.L - 0.0894841775 * o.a - 1.2914855480 * o.b
        let l = l' * l' * l'
        let m = m' * m' * m'
        let s = s' * s' * s'
        let r = 4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s
        let g = -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s
        let b = -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s
        { R = delinearize r; G = delinearize g; B = delinearize b; A = alpha }

    /// OKLab -> sRGB Color (alpha = 1).
    let ofOklab (o: Oklab) : Color = ofOklabA 1.0 o

    /// OKLab -> OKLCH (hue in degrees).
    let toOklchOf (o: Oklab) : Oklch =
        { L = o.L
          C = sqrt (o.a * o.a + o.b * o.b)
          H = normHue (atan2 o.b o.a * 180.0 / Math.PI) }

    /// OKLCH -> OKLab.
    let ofOklchToLab (o: Oklch) : Oklab =
        let hr = o.H * Math.PI / 180.0
        { L = o.L; a = o.C * cos hr; b = o.C * sin hr }

    /// sRGB Color -> OKLCH.
    let toOklch (c: Color) : Oklch = c |> toOklab |> toOklchOf

    /// OKLCH -> sRGB Color, carrying alpha.
    let ofOklchA (alpha: float) (o: Oklch) : Color = o |> ofOklchToLab |> ofOklabA alpha

    /// OKLCH -> sRGB Color (alpha = 1).
    let ofOklch (o: Oklch) : Color = ofOklchA 1.0 o

    // --- HSL / HSV convenience ---

    let toHsl (c: Color) : Hsl =
        let mx = max c.R (max c.G c.B)
        let mn = min c.R (min c.G c.B)
        let l = (mx + mn) / 2.0
        let d = mx - mn
        if d < 1e-12 then { H = 0.0; S = 0.0; L = l }
        else
            let s = d / (1.0 - abs (2.0 * l - 1.0))
            let h =
                if mx = c.R then 60.0 * (((c.G - c.B) / d) % 6.0)
                elif mx = c.G then 60.0 * ((c.B - c.R) / d + 2.0)
                else 60.0 * ((c.R - c.G) / d + 4.0)
            { H = normHue h; S = s; L = l }

    let ofHslA (alpha: float) (h: Hsl) : Color =
        let c = (1.0 - abs (2.0 * h.L - 1.0)) * h.S
        let hp = normHue h.H / 60.0
        let x = c * (1.0 - abs (hp % 2.0 - 1.0))
        let r1, g1, b1 =
            if hp < 1.0 then c, x, 0.0
            elif hp < 2.0 then x, c, 0.0
            elif hp < 3.0 then 0.0, c, x
            elif hp < 4.0 then 0.0, x, c
            elif hp < 5.0 then x, 0.0, c
            else c, 0.0, x
        let m = h.L - c / 2.0
        { R = r1 + m; G = g1 + m; B = b1 + m; A = alpha }

    let ofHsl (h: Hsl) : Color = ofHslA 1.0 h

    let toHsv (c: Color) : Hsv =
        let mx = max c.R (max c.G c.B)
        let mn = min c.R (min c.G c.B)
        let d = mx - mn
        let s = if mx < 1e-12 then 0.0 else d / mx
        let h =
            if d < 1e-12 then 0.0
            elif mx = c.R then 60.0 * (((c.G - c.B) / d) % 6.0)
            elif mx = c.G then 60.0 * ((c.B - c.R) / d + 2.0)
            else 60.0 * ((c.R - c.G) / d + 4.0)
        { H = normHue h; S = s; V = mx }

    let ofHsvA (alpha: float) (h: Hsv) : Color =
        let c = h.V * h.S
        let hp = normHue h.H / 60.0
        let x = c * (1.0 - abs (hp % 2.0 - 1.0))
        let r1, g1, b1 =
            if hp < 1.0 then c, x, 0.0
            elif hp < 2.0 then x, c, 0.0
            elif hp < 3.0 then 0.0, c, x
            elif hp < 4.0 then 0.0, x, c
            elif hp < 5.0 then x, 0.0, c
            else c, 0.0, x
        let m = h.V - c
        { R = r1 + m; G = g1 + m; B = b1 + m; A = alpha }

    let ofHsv (h: Hsv) : Color = ofHsvA 1.0 h

    // =====================================================================
    // TRANSFORMS — operate in OKLCH, clamp on the way out.
    // =====================================================================

    /// Clamp R/G/B/A each to [0,1]. The CHEAP gamut map (a chroma-reducing map is
    /// the "correct" version; this pragmatic clamp is what `toHex` also applies).
    let clampToGamut (c: Color) : Color =
        { R = clamp01 c.R; G = clamp01 c.G; B = clamp01 c.B; A = clamp01 c.A }

    let private mapOklch f (c: Color) =
        let o = toOklch c
        ofOklchA c.A (f o)

    /// Raise OKLCH lightness by `amt` (L clamped to [0,1]).
    let lighten (amt: float) (c: Color) : Color =
        mapOklch (fun o -> { o with L = clamp01 (o.L + amt) }) c |> clampToGamut

    /// Lower OKLCH lightness by `amt`.
    let darken (amt: float) (c: Color) : Color = lighten -amt c

    /// Raise OKLCH chroma by `amt` (C clamped >= 0).
    let saturate (amt: float) (c: Color) : Color =
        mapOklch (fun o -> { o with C = max 0.0 (o.C + amt) }) c |> clampToGamut

    /// Lower OKLCH chroma by `amt`.
    let desaturate (amt: float) (c: Color) : Color = saturate -amt c

    /// Rotate OKLCH hue by `deg` degrees (wraps mod 360).
    let rotateHue (deg: float) (c: Color) : Color =
        mapOklch (fun o -> { o with H = normHue (o.H + deg) }) c |> clampToGamut

    /// PERCEPTUAL mix at t in [0,1]: lerp L/a/b and alpha in OKLab (NOT sRGB lerp).
    /// `mix 0 a b = a`, `mix 1 a b = b`.
    let mix (t: float) (a: Color) (b: Color) : Color =
        let t = clamp01 t
        let oa = toOklab a
        let ob = toOklab b
        let lerp x y = x + (y - x) * t
        ofOklabA (lerp a.A b.A) { L = lerp oa.L ob.L; a = lerp oa.a ob.a; b = lerp oa.b ob.b }
        |> clampToGamut

    /// Replace alpha (clamped to [0,1]).
    let withAlpha (alpha: float) (c: Color) : Color = { c with A = clamp01 alpha }

    // =====================================================================
    // QUERIES — WCAG.
    // =====================================================================

    /// WCAG relative luminance (linearized channels). Ignores alpha.
    let relativeLuminance (c: Color) : float =
        let r, g, b = toLinearRgb c
        0.2126 * r + 0.7152 * g + 0.0722 * b

    /// WCAG contrast ratio in [1,21]; symmetric in its arguments.
    let contrastRatio (a: Color) (b: Color) : float =
        let la = relativeLuminance a
        let lb = relativeLuminance b
        let hi = max la lb
        let lo = min la lb
        (hi + 0.05) / (lo + 0.05)

    let black : Color = { R = 0.0; G = 0.0; B = 0.0; A = 1.0 }
    let white : Color = { R = 1.0; G = 1.0; B = 1.0; A = 1.0 }

    /// Pure black or pure white — whichever has higher contrast with `bg`.
    let readableOn (bg: Color) : Color =
        if contrastRatio white bg >= contrastRatio black bg then white else black

    /// The candidate with the highest contrast against `bg` (the overload).
    /// Empty set -> `readableOn bg`.
    let readableFrom (candidates: Color seq) (bg: Color) : Color =
        let xs = Seq.toList candidates
        match xs with
        | [] -> readableOn bg
        | _ -> xs |> List.maxBy (fun c -> contrastRatio c bg)

    // =====================================================================
    // HARMONIES — TWO meanings of "opposite", documented.
    // =====================================================================

    /// AESTHETIC opposite: the hue-wheel opposite (OKLCH H+180). "Looks opposite"
    /// / decorative — NOT necessarily readable against the original.
    let complement (c: Color) : Color = rotateHue 180.0 c

    /// The two triadic partners (hue +120, +240).
    let triadic (c: Color) : Color list = [ rotateHue 120.0 c; rotateHue 240.0 c ]

    /// Two analogous neighbors `deg` either side on the hue wheel.
    let analogous (deg: float) (c: Color) : Color list = [ rotateHue -deg c; rotateHue deg c ]

    /// Split-complement partners (hue +150, +210).
    let splitComplement (c: Color) : Color list = [ rotateHue 150.0 c; rotateHue 210.0 c ]

    /// LEGIBILITY opposite: black or white, whichever is readable ON `c`. Use this
    /// for TEXT on a background (distinct from `complement`, the decorative hue flip).
    let contrastTo (c: Color) : Color = readableOn c

    // =====================================================================
    // NEAREST (flagship) — perceptual, in OKLab.
    // =====================================================================

    /// Perceptual distance: euclidean in OKLab (NOT sRGB euclid). `deltaE c c = 0`,
    /// symmetric.
    let deltaE (a: Color) (b: Color) : float =
        let oa = toOklab a
        let ob = toOklab b
        let dL = oa.L - ob.L
        let da = oa.a - ob.a
        let db = oa.b - ob.b
        sqrt (dL * dL + da * da + db * db)

    /// The candidate minimizing `deltaE c`. Returns `c` itself if present
    /// (deltaE 0 <= any other). Empty `candidates` -> `c` (total; documented).
    let nearest (candidates: Color seq) (c: Color) : Color =
        let mutable best = c
        let mutable bestD = infinity
        let mutable any = false
        for cand in candidates do
            any <- true
            let d = deltaE c cand
            if d < bestD then
                bestD <- d
                best <- cand
        if any then best else c
