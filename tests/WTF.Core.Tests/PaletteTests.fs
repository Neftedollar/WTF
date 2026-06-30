module WTF.Core.Tests.PaletteTests

open Xunit
open FsCheck
open FsCheck.FSharp
open FsCheck.Xunit
open WTF.Core
open WTF.Core.Color
open WTF.Core.Palette

// =====================================================================
//  The GENERATIVE palette: the Ramp curve primitive, semantic roles,
//  ofSeed / ofColors (determinism + order-independence), the algebra,
//  and the built-in default.
// =====================================================================

let private hx s = Color.ofHexOr Color.black s

/// Circular distance between two hue angles, in [0,180].
let private hueDist (a: float) (b: float) =
    let d = abs (a - b) % 360.0
    min d (360.0 - d)

// ---- Ramp: the vector primitive ----

[<Fact>]
let ``ofStops endpoints sample to first and last`` () =
    let a = hx "#000000"
    let b = hx "#ffffff"
    let r = Ramp.ofStops [ (0.0, a); (1.0, b) ]
    Assert.Equal(toHex a, toHex (r 0.0))
    Assert.Equal(toHex b, toHex (r 1.0))

[<Fact>]
let ``ofStops clamps t outside [0,1]`` () =
    let a = hx "#000000"
    let b = hx "#ffffff"
    let r = Ramp.ofStops [ (0.0, a); (1.0, b) ]
    Assert.Equal(toHex a, toHex (r -5.0))
    Assert.Equal(toHex b, toHex (r 5.0))

[<Fact>]
let ``single-stop ramp is constant`` () =
    let a = hx "#abcdef"
    let r = Ramp.ofStops [ (0.5, a) ]
    Assert.Equal(toHex a, toHex (r 0.0))
    Assert.Equal(toHex a, toHex (r 1.0))

[<Fact>]
let ``samples n returns n colors; samples 1 = [r 0]`` () =
    let r = Ramp.oklchSweep 0.7 0.12 0.0 120.0
    Assert.Equal(5, List.length (Ramp.samples 5 r))
    Assert.Equal(1, List.length (Ramp.samples 1 r))
    Assert.Equal(toHex (r 0.0), toHex (List.head (Ramp.samples 1 r)))

[<Property>]
let ``oklchSweep hue at t matches hue0 + t*span`` () =
    Prop.forAll (Gen.choose (0, 100) |> Arb.fromGen) (fun ti ->
        let t = float ti / 100.0
        let hue0, span = 30.0, 120.0
        let r = Ramp.oklchSweep 0.7 0.12 hue0 span
        let h = (toOklch (r t)).H
        let expected = (hue0 + t * span) % 360.0
        hueDist h expected < 1.0)

[<Fact>]
let ``oklchSweep accent ramp is non-constant`` () =
    let r = (ofSeed (toOklch (hx "#89b4fa"))).Accents
    Assert.True(deltaE (r 0.0) (r 1.0) > 1e-3)

// ---- roles ----

[<Fact>]
let ``ofSeed Text is readable on Base`` () =
    let p = ofSeed (toOklch (hx "#89b4fa"))
    Assert.True(contrastRatio p.Text p.Base >= 4.5)

[<Fact>]
let ``ofSeed Surface and Overlay are lighter than Base`` () =
    let p = ofSeed (toOklch (hx "#cba6f7"))
    Assert.True((toOklch p.Surface).L > (toOklch p.Base).L)
    Assert.True((toOklch p.Overlay).L > (toOklch p.Surface).L)

[<Fact>]
let ``Subtext sits between Text and Base in contrast`` () =
    let p = ofSeed (toOklch (hx "#89b4fa"))
    Assert.True(contrastRatio p.Subtext p.Base < contrastRatio p.Text p.Base)

// ---- determinism ----

let private rolesEq (a: Palette) (b: Palette) =
    toHex a.Base = toHex b.Base
    && toHex a.Surface = toHex b.Surface
    && toHex a.Overlay = toHex b.Overlay
    && toHex a.Text = toHex b.Text
    && toHex a.Subtext = toHex b.Subtext
    && [ 0.0; 0.25; 0.5; 0.75; 1.0 ]
       |> List.forall (fun t -> toHex (a.Accents t) = toHex (b.Accents t))

[<Fact>]
let ``ofSeed is deterministic`` () =
    let seed = toOklch (hx "#89b4fa")
    Assert.True(rolesEq (ofSeed seed) (ofSeed seed))

[<Fact>]
let ``ofColors is order-independent`` () =
    let cs = [ hx "#1e1e2e"; hx "#89b4fa"; hx "#cdd6f4"; hx "#f38ba8"; hx "#a6e3a1" ]
    let shuffled = [ hx "#f38ba8"; hx "#cdd6f4"; hx "#a6e3a1"; hx "#1e1e2e"; hx "#89b4fa" ]
    Assert.True(rolesEq (ofColors cs) (ofColors shuffled))

[<Fact>]
let ``ofColors picks darkest as Base and high-contrast Text`` () =
    let cs = [ hx "#1e1e2e"; hx "#89b4fa"; hx "#cdd6f4" ]
    let p = ofColors cs
    Assert.Equal(toHex (hx "#1e1e2e"), toHex p.Base)
    Assert.True(contrastRatio p.Text p.Base >= 4.5)

[<Fact>]
let ``ofColors [] = defaultPalette`` () =
    Assert.True(rolesEq (ofColors []) defaultPalette)

// ---- algebra ----

[<Fact>]
let ``darken lowers every role lightness`` () =
    let p = defaultPalette
    let d = darken 0.1 p
    Assert.True((toOklch d.Base).L < (toOklch p.Base).L + 1e-9)
    Assert.True((toOklch d.Text).L <= (toOklch p.Text).L + 1e-9)

[<Fact>]
let ``map applies to roles AND the ramp`` () =
    let p = defaultPalette
    let m = map (Color.rotateHue 180.0) p
    Assert.NotEqual<string>(toHex p.Base, toHex m.Base)
    Assert.NotEqual<string>(toHex (p.Accents 0.5), toHex (m.Accents 0.5))

[<Fact>]
let ``withContrast restores legible Text after a transform`` () =
    let lightened = lighten 0.6 defaultPalette // base now bright -> Text may be illegible
    let fixed' = withContrast lightened
    Assert.True(contrastRatio fixed'.Text fixed'.Base >= 4.5)

[<Fact>]
let ``blend 0 = a, blend 1 = b on roles`` () =
    let a = defaultPalette
    let b = ofSeed (toOklch (hx "#f38ba8"))
    Assert.Equal(toHex a.Base, toHex ((blend 0.0 a b).Base))
    Assert.Equal(toHex b.Base, toHex ((blend 1.0 a b).Base))

[<Fact>]
let ``accent samples the ramp`` () =
    let p = defaultPalette
    Assert.Equal(toHex (p.Accents 0.3), toHex (accent 0.3 p))

[<Fact>]
let ``nearestRole snaps to the closest role`` () =
    let p = defaultPalette
    // a color essentially equal to Text should snap to Text
    let near = Color.lighten 0.001 p.Text
    Assert.Equal(toHex p.Text, toHex (nearestRole near p))

// ---- default ----

[<Fact>]
let ``defaultPalette Text readable on Base`` () =
    Assert.True(contrastRatio defaultPalette.Text defaultPalette.Base >= 4.5)
