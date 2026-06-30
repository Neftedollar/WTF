module WTF.Core.Tests.LayoutTests

open FsCheck.Xunit
open WTF.Core

open Xunit

let private screen = Rect.create 0 0 1920 1080

/// On-screen AND non-degenerate. The non-negativity clause is load-bearing:
/// without it an inverted rect (large X, very negative W) trivially satisfies
/// the edge tests (X+W stays <= the right edge), so every "stays on screen"
/// property would pass vacuously on garbage tiles. This is exactly what let the
/// old unclamped pad/ratio bugs slip through.
let private inside (a: Rect) (r: Rect) =
    r.Width >= 0<lpx> && r.Height >= 0<lpx>
    && r.X >= a.X && r.Y >= a.Y
    && r.X + r.Width <= a.X + a.Width
    && r.Y + r.Height <= a.Y + a.Height

// ---- exact-tiling helpers (interiors, so zero-size tiles never "overlap") --
let private overlaps (a: Rect) (b: Rect) =
    a.X < b.X + b.Width && b.X < a.X + a.Width
    && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height

let rec private pairwiseDisjoint =
    function
    | [] | [ _ ] -> true
    | r :: rest -> List.forall (fun o -> not (overlaps r o)) rest && pairwiseDisjoint rest

let private sumArea rs = rs |> List.sumBy (fun (r: Rect) -> int r.Width * int r.Height)

/// Tiles exactly cover `a`: all inside, pairwise disjoint, and areas sum to the
/// whole (disjoint + inside + area-complete ⟹ union == a, for integer rects).
let private tilesExactly (a: Rect) (rs: Rect list) =
    List.forall (inside a) rs
    && pairwiseDisjoint rs
    && sumArea rs = int a.Width * int a.Height

let private mk n = Stack.ofList [ 1 .. n ] |> Option.get   // n>=1 windows

// A layout must place exactly one rect per window, and never escape the screen.

[<Property>]
let ``tall places one rect per window`` (s: Stack<int>) =
    List.length (Layout.tall 1 0.5 screen s) = Stack.length s

[<Property>]
let ``tall keeps every tile on screen`` (s: Stack<int>) =
    Layout.tall 1 0.5 screen s |> List.forall (fun (_, r) -> inside screen r)

[<Property>]
let ``bsp places one rect per window`` (s: Stack<int>) =
    List.length (Layout.bsp screen s) = Stack.length s

[<Property>]
let ``bsp keeps every tile on screen`` (s: Stack<int>) =
    Layout.bsp screen s |> List.forall (fun (_, r) -> inside screen r)

[<Property>]
let ``full gives every window the whole screen`` (s: Stack<int>) =
    Layout.full screen s |> List.forall (fun (_, r) -> r = screen)

[<Property>]
let ``gaps keep tiles strictly inside the screen`` (s: Stack<int>) =
    Layout.withGaps 10<lpx> (Layout.tall 1 0.5) screen s
    |> List.forall (fun (_, r) -> inside screen r)

[<Property>]
let ``grid places one rect per window, all on screen`` (s: Stack<int>) =
    let placed = Layout.grid screen s
    List.length placed = Stack.length s
    && List.forall (fun (_, r) -> inside screen r) placed

[<Property>]
let ``mirror keeps tiles on screen and count intact`` (s: Stack<int>) =
    let placed = Layout.mirror (Layout.tall 1 0.5) screen s
    List.length placed = Stack.length s
    && List.forall (fun (_, r) -> inside screen r) placed

[<Property>]
let ``mirror is an involution on a square screen`` (s: Stack<int>) =
    // mirroring twice returns the original placement (transpose is self-inverse)
    let square = Rect.create 0 0 1000 1000
    let once = Layout.mirror (Layout.tall 1 0.5) square s
    let twice = Layout.mirror (Layout.mirror (Layout.tall 1 0.5)) square s
    once = once && twice = Layout.tall 1 0.5 square s

// =====================================================================
//  Non-negativity: EVERY layout (and withGaps) must place only
//  non-degenerate rects. Asserted directly via the strengthened `inside`.
// =====================================================================

let private rectsOf placed = placed |> List.map snd

[<Property>]
let ``no layout ever emits a negative-dimension tile`` (s: Stack<int>) =
    let all =
        [ Layout.tall 1 0.5; Layout.tall 2 0.3; Layout.bsp; Layout.grid; Layout.full
          Layout.mirror (Layout.tall 1 0.5); Layout.withGaps 12<lpx> (Layout.tall 1 0.5) ]
    all |> List.forall (fun lay -> lay screen s |> List.forall (fun (_, r) -> inside screen r))

// Regression for the unclamped pad/ratio bugs: a gap larger than half the
// screen must clamp to a zero-size tile, never invert into a negative rect.
[<Fact>]
let ``withGaps larger than half the screen clamps instead of inverting`` () =
    let placed = Layout.withGaps 2000<lpx> (Layout.tall 1 0.5) screen (mk 3)
    Assert.All(rectsOf placed, fun r ->
        Assert.True(r.Width >= 0<lpx> && r.Height >= 0<lpx>, sprintf "inverted: %A" r))

[<Fact>]
let ``tall with out-of-range ratio never inverts a tile`` () =
    for ratio in [ -1.0; 0.0; 1.0; 2.0; 5.0 ] do
        let placed = Layout.tall 1 ratio screen (mk 4)
        Assert.All(rectsOf placed, fun r ->
            Assert.True(r.Width >= 0<lpx> && r.Height >= 0<lpx>, sprintf "ratio %f -> %A" ratio r))

// =====================================================================
//  Exact tiling: tiles cover the screen with no overlap (stronger than
//  on-screen). Covers tall/bsp and the perfect-square grid cases.
// =====================================================================

[<Fact>]
let ``tall tiles the screen exactly across n, nmaster and ratio`` () =
    for n in 1 .. 8 do
        for nmaster in 0 .. 4 do          // 0 and negative clamp to 1
            for ratio in [ 0.0; 0.25; 0.5; 0.75; 1.0 ] do
                let rs = rectsOf (Layout.tall nmaster ratio screen (mk n))
                Assert.True(tilesExactly screen rs,
                            sprintf "tall n=%d nmaster=%d ratio=%f did not tile exactly" n nmaster ratio)

[<Fact>]
let ``tall clamps negative nmaster to a single master`` () =
    // nmaster = -3 must behave identically to nmaster = 1
    let a = Layout.tall -3 0.5 screen (mk 5)
    let b = Layout.tall 1 0.5 screen (mk 5)
    Assert.Equal<(int * Rect) list>(b, a)

[<Fact>]
let ``bsp tiles the screen exactly`` () =
    for n in 1 .. 8 do
        let rs = rectsOf (Layout.bsp screen (mk n))
        Assert.True(tilesExactly screen rs, sprintf "bsp n=%d did not tile exactly" n)

[<Fact>]
let ``grid tiles a perfect square exactly and is always disjoint`` () =
    for n in [ 1; 4; 9; 16 ] do
        let rs = rectsOf (Layout.grid screen (mk n))
        Assert.True(tilesExactly screen rs, sprintf "grid n=%d (perfect square) did not tile exactly" n)
    for n in 1 .. 12 do                    // incl. non-square 3,5,7,...: no overlap, all on screen
        let rs = rectsOf (Layout.grid screen (mk n))
        Assert.True(List.forall (inside screen) rs && pairwiseDisjoint rs,
                    sprintf "grid n=%d overlapped or escaped" n)

[<Fact>]
let ``grid full-row columns abut and cover the width`` () =
    // n=6 -> 3 cols x 2 rows, both rows full: each row's columns must abut and
    // span the full screen width with no seam.
    let rs = rectsOf (Layout.grid screen (mk 6))
    let row0 = rs |> List.filter (fun r -> r.Y = screen.Y) |> List.sortBy (fun r -> r.X)
    Assert.Equal(3, row0.Length)
    Assert.Equal(screen.X, row0.Head.X)
    List.pairwise row0 |> List.iter (fun (l, r) -> Assert.Equal(l.X + l.Width, r.X))
    let last = List.last row0
    Assert.Equal(screen.X + screen.Width, last.X + last.Width)

// =====================================================================
//  Mirror on a NON-square area (square hides W/H transpose bugs).
// =====================================================================

[<Property>]
let ``mirror is an involution on a non-square screen`` (s: Stack<int>) =
    let twice = Layout.mirror (Layout.mirror (Layout.tall 1 0.5)) screen s
    twice = Layout.tall 1 0.5 screen s

[<Property>]
let ``mirror keeps tiles on a non-square screen with count intact`` (s: Stack<int>) =
    let placed = Layout.mirror (Layout.bsp) screen s
    List.length placed = Stack.length s && List.forall (fun (_, r) -> inside screen r) placed

// =====================================================================
//  reflectHoriz (was entirely untested; area0 is intentionally ignored).
// =====================================================================

[<Property>]
let ``reflectHoriz preserves Width Y Height and reflects X to the mirror gap`` (s: Stack<int>) =
    let baseL = Layout.tall 2 0.5
    let orig = baseL screen s
    let refl = Layout.reflectHoriz screen baseL screen s
    List.length refl = List.length orig
    && List.forall2 (fun (_, r: Rect) (_, m: Rect) ->
        m.Width = r.Width && m.Y = r.Y && m.Height = r.Height
        // reflected tile's right gap from the edge == original left gap
        && (screen.X + screen.Width) - (m.X + m.Width) = r.X - screen.X) orig refl

[<Property>]
let ``reflectHoriz is an involution`` (s: Stack<int>) =
    let baseL = Layout.tall 2 0.5
    let once = Layout.reflectHoriz screen baseL
    let twice = Layout.reflectHoriz screen once
    twice screen s = baseL screen s

[<Fact>]
let ``reflectHoriz ignores its area0 parameter`` () =
    // Passing a wildly different area0 must NOT change the result: the body uses
    // the runtime area, not area0. Locks the documented (dead) contract.
    let s = mk 4
    let a = Layout.reflectHoriz screen (Layout.tall 1 0.5) screen s
    let b = Layout.reflectHoriz (Rect.create 999 999 7 7) (Layout.tall 1 0.5) screen s
    Assert.Equal<(int * Rect) list>(a, b)
