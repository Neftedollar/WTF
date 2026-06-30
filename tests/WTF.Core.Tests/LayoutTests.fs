module WTF.Core.Tests.LayoutTests

open FsCheck.Xunit
open WTF.Core

let private screen = Rect.create 0 0 1920 1080

let private inside (a: Rect) (r: Rect) =
    r.X >= a.X && r.Y >= a.Y
    && r.X + r.Width <= a.X + a.Width
    && r.Y + r.Height <= a.Y + a.Height

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
    Layout.withGaps 10 (Layout.tall 1 0.5) screen s
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
