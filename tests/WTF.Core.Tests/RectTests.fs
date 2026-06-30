module WTF.Core.Tests.RectTests

open Xunit
open FsCheck
open FsCheck.Xunit
open WTF.Core

// =====================================================================
//  Rect.pad — directly tested for the first time. The key invariant is
//  that an oversized gap clamps to a zero-size tile rather than inverting
//  into a negative-dimension rect (the unclamped-pad bug).
// =====================================================================

let private screen = Rect.create 0 0 1920 1080

[<Property>]
let ``pad 0 is the identity`` (x: int) (y: int) (w: int) (h: int) =
    let r = Rect.create x y w h
    Rect.pad 0<lpx> r = r

[<Fact>]
let ``pad shrinks by gap on every side and by 2*gap in size`` () =
    let r = Rect.create 100 200 800 600
    let p = Rect.pad 10<lpx> r
    Assert.Equal(110<lpx>, p.X)
    Assert.Equal(210<lpx>, p.Y)
    Assert.Equal(780<lpx>, p.Width)   // 800 - 2*10
    Assert.Equal(580<lpx>, p.Height)  // 600 - 2*10

[<Property>]
let ``pad never produces a negative dimension for any non-negative gap`` (g: int) =
    let gap = abs g * 1<lpx>
    let p = Rect.pad gap screen
    p.Width >= 0<lpx> && p.Height >= 0<lpx>

[<Fact>]
let ``pad with gap larger than half the dimension clamps to zero, not negative`` () =
    // 1920x1080 with a 5000px gap: both axes clamp; size is 0 or 1 (parity), not negative.
    let p = Rect.pad 5000<lpx> screen
    Assert.True(p.Width >= 0<lpx> && p.Width <= 1<lpx>)
    Assert.True(p.Height >= 0<lpx> && p.Height <= 1<lpx>)

[<Fact>]
let ``pad treats a negative gap as a no-op, never an expand`` () =
    let r = Rect.create 0 0 100 100
    Assert.Equal(r, Rect.pad -10<lpx> r)

// =====================================================================
//  splitVertical / splitHorizontal — exact tiling for ANY ratio, plus
//  the ratio clamp (no negative halves out of [0,1]).
// =====================================================================

let private clampRatio (r: float) =
    if System.Double.IsNaN r then 0.0 else max 0.0 (min 1.0 r)

[<Property>]
let ``splitVertical halves abut and exactly cover the original`` (ratio: NormalFloat) =
    let r = screen
    let l, right = Rect.splitVertical (clampRatio ratio.Get) r
    l.X = r.X && l.Y = r.Y && l.Height = r.Height
    && right.Y = r.Y && right.Height = r.Height
    && l.X + l.Width = right.X                       // abut, no seam
    && l.Width + right.Width = r.Width               // cover, no overlap
    && l.Width >= 0<lpx> && right.Width >= 0<lpx>    // neither half inverted

[<Property>]
let ``splitHorizontal halves abut and exactly cover the original`` (ratio: NormalFloat) =
    let r = screen
    let top, bot = Rect.splitHorizontal (clampRatio ratio.Get) r
    top.X = r.X && top.Y = r.Y && top.Width = r.Width
    && bot.X = r.X && bot.Width = r.Width
    && top.Y + top.Height = bot.Y
    && top.Height + bot.Height = r.Height
    && top.Height >= 0<lpx> && bot.Height >= 0<lpx>

[<Fact>]
let ``splitVertical clamps a ratio above 1 to a zero-width right half`` () =
    let l, r = Rect.splitVertical 5.0 screen
    Assert.Equal(screen.Width, l.Width)
    Assert.Equal(0<lpx>, r.Width)
    Assert.Equal(screen.X + screen.Width, r.X)

[<Fact>]
let ``splitVertical clamps a negative ratio to a zero-width left half`` () =
    let l, r = Rect.splitVertical -2.0 screen
    Assert.Equal(0<lpx>, l.Width)
    Assert.Equal(screen.Width, r.Width)

// =====================================================================
//  columnOf — sliced rows abut, last row absorbs the remainder, the
//  column is covered exactly, and n<=0 is empty.
// =====================================================================

[<Property>]
let ``columnOf with n<=0 is empty`` (n: int) =
    n > 0 || Rect.columnOf n screen = []

[<Fact>]
let ``columnOf tiles the column exactly for n = 1..many`` () =
    let r = Rect.create 10 20 300 1003   // odd height to exercise the remainder
    for n in 1 .. 17 do
        let rows = Rect.columnOf n r
        Assert.Equal(n, rows.Length)
        // every row spans the full width at the original X
        Assert.All(rows, fun row -> Assert.Equal(r.X, row.X); Assert.Equal(r.Width, row.Width))
        // rows abut top-to-bottom
        List.pairwise rows |> List.iter (fun (a, b) -> Assert.Equal(a.Y + a.Height, b.Y))
        // first starts at the top, last ends exactly at the bottom (remainder absorbed)
        Assert.Equal(r.Y, rows.Head.Y)
        let last = List.last rows
        Assert.Equal(r.Y + r.Height, last.Y + last.Height)
        // total height == original (exact cover)
        Assert.Equal(r.Height, rows |> List.sumBy (fun x -> x.Height))

// =====================================================================
//  Rect.area (previously untested).
// =====================================================================

[<Fact>]
let ``area is width times height`` () =
    Assert.Equal(1920 * 1080 * 1<lpx^2>, Rect.area screen)
    Assert.Equal(0<lpx^2>, Rect.area (Rect.create 5 5 0 999))

// =====================================================================
//  Scaling.configure — fractional scale is the whole point of edge-then-
//  subtract: adjacent tiles must still abut (no HiDPI seam), and no scale
//  may yield a negative w/h.
// =====================================================================

[<Fact>]
let ``fractional-scale split tiles abut with no HiDPI seam`` () =
    let left, right = Rect.splitVertical 0.5 screen
    for scale in [ 1.25; 1.5; 2.5 ] do
        let lx, _, lw, _ = Scaling.configure scale left
        let rx, _, _, _ = Scaling.configure scale right
        Assert.True(lx + lw = rx, sprintf "seam at scale %f: %d+%d <> %d" scale lx lw rx)

[<Property>]
let ``configure never emits a negative width or height at any positive scale`` (x: int) (y: int) (w: int) (h: int) (k: int) =
    let scale = 1.0 + float (abs k % 400) / 100.0     // 1.0 .. 4.99
    let r = Rect.create x y (abs w) (abs h)
    let _, _, cw, ch = Scaling.configure scale r
    cw >= 0 && ch >= 0

// =====================================================================
//  Px conversions — document banker's (round-to-even) behaviour and the
//  fractional round-trip loss. These pin CURRENT behaviour so a change
//  (e.g. switching to away-from-zero rounding) is a conscious decision.
// =====================================================================

[<Fact>]
let ``toPhysical uses banker's rounding at exact half pixels`` () =
    // round-to-even: 0.5 -> 0 (a 1px window can vanish when downscaling at .5)
    Assert.Equal(0<ppx>, Px.toPhysical 0.5 1<lpx>)     // round(0.5) = 0
    Assert.Equal(2<ppx>, Px.toPhysical 0.5 3<lpx>)     // round(1.5) = 2
    Assert.Equal(2<ppx>, Px.toPhysical 0.5 5<lpx>)     // round(2.5) = 2 (to even)
    Assert.Equal(4<ppx>, Px.toPhysical 0.5 7<lpx>)     // round(3.5) = 4

[<Fact>]
let ``toLogical round-trips at integer scale but loses at fractional scale`` () =
    Assert.Equal(137<lpx>, Px.toLogical 2.0 (Px.toPhysical 2.0 137<lpx>))
    // at 1.5 the forward map rounds, so the inverse is lossy for some values
    let v = 3<lpx>
    let back = Px.toLogical 1.5 (Px.toPhysical 1.5 v)   // round(4.5)=4 -> round(2.667)=3
    Assert.Equal(3<lpx>, back)
    let v2 = 1<lpx>
    let back2 = Px.toLogical 1.5 (Px.toPhysical 1.5 v2) // round(1.5)=2 -> round(1.333)=1
    Assert.Equal(1<lpx>, back2)
