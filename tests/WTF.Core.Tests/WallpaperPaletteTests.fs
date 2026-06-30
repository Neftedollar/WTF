[<Xunit.Collection("Wallpaper")>]
module WTF.Core.Tests.WallpaperPaletteTests

// Host-side wallpaper palette EXTRACTION (impure, ImageSharp) — covered via the
// `internal` surface (InternalsVisibleTo WTF.Core.Tests). Asserts the median-cut
// quantization is DETERMINISTIC and reproducible against a committed fixture image
// (`fixtures/quadrants.png`: four equal quadrants — red / green / blue / dark
// slate), and that `paletteOf` STRUCTURES the raw list into a generative Palette.

open System
open System.IO
open Xunit
open WTF.Core
open WTF.Core.Color
open WTF.Host

let private fixture =
    Path.Combine(AppContext.BaseDirectory, "fixtures", "quadrants.png")

// The four quadrant colors the fixture was authored with (sRGB 0..1).
let private redQ = ofRgbTuple (220.0 / 255.0, 30.0 / 255.0, 40.0 / 255.0)
let private greenQ = ofRgbTuple (40.0 / 255.0, 200.0 / 255.0, 60.0 / 255.0)
let private blueQ = ofRgbTuple (50.0 / 255.0, 70.0 / 255.0, 210.0 / 255.0)
let private darkQ = ofRgbTuple (24.0 / 255.0, 24.0 / 255.0, 36.0 / 255.0)

[<Fact>]
let ``dominantColors returns [] for a missing image (best-effort)`` () =
    Assert.Empty(Wallpaper.dominantColors 8 "/nonexistent/zzz-no-such.png")

[<Fact>]
let ``dominantColors is deterministic across runs`` () =
    let a = Wallpaper.dominantColors 4 fixture
    let b = Wallpaper.dominantColors 4 fixture
    Assert.Equal<Color list>(a, b)

[<Fact>]
let ``dominantColors returns exactly n boxes for the fixture`` () =
    Assert.Equal(4, (Wallpaper.dominantColors 4 fixture).Length)

[<Fact>]
let ``dominantColors is sorted by luminance ascending`` () =
    let cs = Wallpaper.dominantColors 4 fixture
    let lums = cs |> List.map relativeLuminance
    Assert.Equal<float list>(List.sort lums, lums)

[<Fact>]
let ``dominantColors recovers each quadrant color within tolerance`` () =
    let cs = Wallpaper.dominantColors 4 fixture
    // Each authored quadrant has a near match (perceptual deltaE) among the boxes.
    for expected in [ redQ; greenQ; blueQ; darkQ ] do
        let d = cs |> List.map (deltaE expected) |> List.min
        Assert.True(d < 0.12, sprintf "no extracted color near %A (min deltaE %f)" expected d)

[<Fact>]
let ``dominantColors darkest box is the dark-slate quadrant`` () =
    // Sorted ascending => head is the darkest; it is the slate quadrant.
    let head = (Wallpaper.dominantColors 4 fixture) |> List.head
    Assert.True(deltaE head darkQ < 0.12)

[<Fact>]
let ``paletteOf an Image structures the extraction into a Palette`` () =
    let p = Wallpaper.paletteOf (Image(fixture, Fill))
    // ofColors makes Base = darkest dominant color => the slate quadrant.
    Assert.True(deltaE p.Base darkQ < 0.12)
    // Text is the most readable of the dominants on Base (not the default gray).
    Assert.True(Color.contrastRatio p.Text p.Base > 3.0)
    // The accent ramp is live (samplable), centered on the highest-chroma color.
    Assert.NotEqual(p.Accents 0.0, p.Accents 1.0)

[<Fact>]
let ``paletteOf a solid Color is a one-seed palette`` () =
    let p = Wallpaper.paletteOf (Wallpaper.Color "#3366cc")
    // ofColors [c] => Base is that single color (it is the darkest = only one).
    Assert.True(deltaE p.Base (ofHexOr black "#3366cc") < 0.001)

[<Fact>]
let ``paletteOf NoWallpaper is the built-in default`` () =
    let p = Wallpaper.paletteOf NoWallpaper
    Assert.Equal(Palette.defaultPalette.Base, p.Base)

[<Fact>]
let ``paletteOf a bad solid Color falls back to the default`` () =
    let p = Wallpaper.paletteOf (Wallpaper.Color "not-a-hex")
    Assert.Equal(Palette.defaultPalette.Base, p.Base)
