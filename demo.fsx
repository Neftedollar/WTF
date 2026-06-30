// Quick ASCII visualiser for the layout engine — no Wayland needed.
//   dotnet fsi demo.fsx
#r "src/WTF.Core/bin/Debug/net10.0/WTF.Core.dll"
open WTF.Core

// Render a (window * Rect) list onto a small character grid.
let render cols rows (placed: (int * Rect) list) =
    let grid = Array2D.create rows cols ' '
    let sx, sy = float cols / 1920.0, float rows / 1080.0
    for (w, r) in placed do
        let x0 = int (float r.X * sx)
        let y0 = int (float r.Y * sy)
        let x1 = int (float (r.X + r.Width)  * sx) - 1
        let y1 = int (float (r.Y + r.Height) * sy) - 1
        let label = char (int '0' + (w % 10))
        for y in max 0 y0 .. min (rows - 1) y1 do
            for x in max 0 x0 .. min (cols - 1) x1 do
                let edge = (x = x0 || x = x1 || y = y0 || y = y1)
                grid.[y, x] <- if edge then '#' else label
    for y in 0 .. rows - 1 do
        System.String(Array.init cols (fun x -> grid.[y, x])) |> printfn "%s"

let screen = Rect.create 0 0 1920 1080
let windows n = { Focus = 1; Up = []; Down = [ 2 .. n ] }  // n windows, ids 1..n

let show name (layout: Layout<int>) n =
    printfn "\n== %s  (%d windows) ==" name n
    render 60 18 (layout screen (windows n))

show "Tall (1 master, 50%%)" (Layout.tall 1 0.5) 4
show "BSP" Layout.bsp 5
show "Tall + gaps" (Layout.withGaps 40<lpx> (Layout.tall 1 0.6)) 3
