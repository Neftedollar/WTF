module WTF.Core.Tests.ColorTests

open Xunit
open FsCheck
open FsCheck.FSharp
open FsCheck.Xunit
open WTF.Core
open WTF.Core.Color

// =====================================================================
//  The PURE color algebra: space round-trips, transforms, WCAG queries,
//  harmonies (the two "opposites"), and the flagship deltaE/nearest.
//  Mostly FsCheck properties over Colors with channels in [0,1].
// =====================================================================

/// A unit float in [0,1] (1000 steps), never NaN/infinity.
let private genUnit : Gen<float> = Gen.map (fun (n: int) -> float n / 1000.0) (Gen.choose (0, 1000))

let private genColor : Gen<Color> =
    gen {
        let! r = genUnit
        let! g = genUnit
        let! b = genUnit
        let! a = genUnit
        return { R = r; G = g; B = b; A = a }
    }

/// An opaque, chromatic, IN-GAMUT color (low chroma at mid lightness, so every
/// hue and its ±rotations stay representable — hue-based tests are well-posed).
let private genChromatic : Gen<Color> =
    Gen.map (fun (h: int) -> Color.ofOklch { L = 0.5; C = 0.03; H = float h }) (Gen.choose (0, 359))

/// Circular distance between two hue angles, in [0,180].
let private hueDist (a: float) (b: float) =
    let d = abs (a - b) % 360.0
    min d (360.0 - d)

let private arbColor = Arb.fromGen genColor
let private arbChromatic = Arb.fromGen genChromatic

let private close eps (a: float) (b: float) = abs (a - b) < eps

// ---- hex round-trips ----

[<Property>]
let ``toHex >> ofHex >> toHex is idempotent (8-bit)`` () =
    Prop.forAll arbColor (fun c ->
        let h = toHex c
        match ofHex h with
        | Some c2 -> toHex c2 = h
        | None -> false)

[<Fact>]
let ``toHex emits rrggbb when opaque, rrggbbaa when translucent`` () =
    Assert.Equal("#ff8800", toHex { R = 1.0; G = 0.533333; B = 0.0; A = 1.0 })
    let h = toHex { R = 1.0; G = 0.0; B = 0.0; A = 0.5 }
    Assert.Equal(9, h.Length)
    Assert.StartsWith("#ff0000", h)

[<Fact>]
let ``ofHex parses rgb, rrggbb, rrggbbaa and rejects garbage`` () =
    Assert.Equal(Some { R = 1.0; G = 1.0; B = 1.0; A = 1.0 }, ofHex "#fff")
    Assert.True((ofHex "#89b4fa").IsSome)
    match ofHex "#11223380" with
    | Some c -> Assert.True(close 0.01 c.A 0.5)
    | None -> Assert.True(false)
    Assert.Equal(None, ofHex "nonsense")
    Assert.Equal(None, ofHex "##fff")
    Assert.Equal(None, ofHex "#12")

[<Fact>]
let ``ofRgbTuple / toRgbTuple bridge the Protocol path`` () =
    let c = ofRgbTuple (0.1, 0.2, 0.3)
    Assert.Equal(1.0, c.A)
    Assert.Equal((0.1, 0.2, 0.3), toRgbTuple c)

// ---- space round-trips ----

[<Property>]
let ``OKLab round-trips`` () =
    Prop.forAll arbColor (fun c ->
        let c2 = c |> toOklab |> ofOklabA c.A
        close 1e-5 c.R c2.R && close 1e-5 c.G c2.G && close 1e-5 c.B c2.B && close 1e-9 c.A c2.A)

[<Property>]
let ``OKLCH round-trips`` () =
    Prop.forAll arbColor (fun c ->
        let c2 = c |> toOklch |> ofOklchA c.A
        close 1e-5 c.R c2.R && close 1e-5 c.G c2.G && close 1e-5 c.B c2.B)

[<Property>]
let ``HSL round-trips`` () =
    Prop.forAll arbColor (fun c ->
        let c2 = c |> toHsl |> ofHslA c.A
        close 1e-6 c.R c2.R && close 1e-6 c.G c2.G && close 1e-6 c.B c2.B)

[<Property>]
let ``HSV round-trips`` () =
    Prop.forAll arbColor (fun c ->
        let c2 = c |> toHsv |> ofHsvA c.A
        close 1e-6 c.R c2.R && close 1e-6 c.G c2.G && close 1e-6 c.B c2.B)

// ---- transforms ----

[<Property>]
let ``lighten raises OKLCH L (when headroom)`` () =
    Prop.forAll arbChromatic (fun c ->
        let l0 = (toOklch c).L
        if l0 > 0.9 then true
        else (toOklch (lighten 0.05 c)).L >= l0 - 1e-9)

[<Property>]
let ``rotateHue 360 is identity`` () =
    Prop.forAll arbChromatic (fun c ->
        let c2 = rotateHue 360.0 c
        close 1e-4 c.R c2.R && close 1e-4 c.G c2.G && close 1e-4 c.B c2.B)

[<Property>]
let ``mix 0 = a and mix 1 = b`` () =
    Prop.forAll (Gen.zip genColor genColor |> Arb.fromGen) (fun (a, b) ->
        let m0 = mix 0.0 a b
        let m1 = mix 1.0 a b
        close 1e-5 m0.R a.R && close 1e-5 m1.R b.R && close 1e-5 m1.G b.G)

[<Fact>]
let ``mix is perceptual (OKLab midpoint of black/white)`` () =
    let m = mix 0.5 black white
    // perceptual midpoint: OKLab L ~ 0.5, which sRGB-wise is #636363 — DISTINCT
    // from the naive sRGB-lerp midpoint #808080 (and perceptually, not numerically,
    // halfway). This is the whole point of mixing in OKLab.
    let lab = toOklab m
    Assert.True(close 0.02 lab.L 0.5)
    Assert.NotEqual<string>("#808080", toHex m)
    Assert.True(relativeLuminance m < relativeLuminance { R = 0.5; G = 0.5; B = 0.5; A = 1.0 })

// ---- WCAG ----

[<Fact>]
let ``contrastRatio black white = 21`` () =
    Assert.True(close 1e-6 (contrastRatio black white) 21.0)

[<Property>]
let ``contrastRatio is symmetric`` () =
    Prop.forAll (Gen.zip genColor genColor |> Arb.fromGen) (fun (a, b) ->
        close 1e-12 (contrastRatio a b) (contrastRatio b a))

[<Fact>]
let ``readableOn dark = white, on light = black`` () =
    Assert.Equal(white, readableOn (ofHexOr black "#1e1e2e"))
    Assert.Equal(black, readableOn (ofHexOr black "#eeeeee"))

[<Fact>]
let ``readableFrom picks max-contrast member`` () =
    let bg = ofHexOr black "#1e1e2e"
    let cands = [ ofHexOr black "#333333"; white; ofHexOr black "#444444" ]
    Assert.Equal(white, readableFrom cands bg)

// ---- harmonies: two meanings of opposite ----

[<Property>]
let ``complement = hue + 180 mod 360`` () =
    Prop.forAll arbChromatic (fun c ->
        let h0 = (toOklch c).H
        let h1 = (toOklch (complement c)).H
        abs (hueDist h0 h1 - 180.0) < 1.0)

[<Fact>]
let ``contrastTo returns black or white (legibility opposite)`` () =
    let c = ofHexOr black "#89b4fa"
    let t = contrastTo c
    Assert.True(t = black || t = white)

[<Fact>]
let ``complement is NOT necessarily readable (distinct from contrastTo)`` () =
    // a mid-grey's hue-complement is still mid-grey -> poor text contrast,
    // whereas contrastTo yields a legible black/white.
    let grey = ofHexOr black "#7f7f7f"
    Assert.True(contrastRatio (complement grey) grey < 4.5)
    Assert.True(contrastRatio (contrastTo grey) grey >= 4.5)

[<Fact>]
let ``triadic / splitComplement return two partners`` () =
    let c = ofHexOr black "#89b4fa"
    Assert.Equal(2, List.length (triadic c))
    Assert.Equal(2, List.length (splitComplement c))
    Assert.Equal(2, List.length (analogous 30.0 c))

// ---- deltaE / nearest (flagship) ----

[<Property>]
let ``deltaE c c = 0 and is symmetric`` () =
    Prop.forAll (Gen.zip genColor genColor |> Arb.fromGen) (fun (a, b) ->
        close 1e-12 (deltaE a a) 0.0 && close 1e-12 (deltaE a b) (deltaE b a))

[<Property>]
let ``nearest returns a member of candidates`` () =
    Prop.forAll (Gen.zip (Gen.nonEmptyListOf genColor) genColor |> Arb.fromGen) (fun (cands, c) ->
        let n = nearest cands c
        List.contains n cands)

[<Property>]
let ``nearest returns c when c is present`` () =
    Prop.forAll (Gen.zip (Gen.listOf genColor) genColor |> Arb.fromGen) (fun (rest, c) ->
        let cands = c :: rest
        deltaE c (nearest cands c) < 1e-12)

[<Property>]
let ``nearest is the closest candidate (deltaE invariant)`` () =
    Prop.forAll (Gen.zip (Gen.nonEmptyListOf genColor) genColor |> Arb.fromGen) (fun (cands, c) ->
        let n = nearest cands c
        let dn = deltaE c n
        cands |> List.forall (fun other -> dn <= deltaE c other + 1e-12))

[<Fact>]
let ``nearest of empty is c`` () =
    let c = ofHexOr black "#abcdef"
    Assert.Equal(c, nearest [] c)
