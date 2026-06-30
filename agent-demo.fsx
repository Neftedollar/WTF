// Simulates an LLM agent driving WTF through the JSON protocol — no compositor.
//   dotnet fsi agent-demo.fsx
#r "src/WTF.Core/bin/Debug/net10.0/WTF.Core.dll"
open WTF.Core

// --- tiny ASCII renderer for the computed arrange ---
let render (w: World) =
    let cols, rows = 56, 14
    let grid = Array2D.create rows cols ' '
    let sx, sy = float cols / float w.Screen.Width, float rows / float w.Screen.Height
    for (id, r) in World.arrange w do
        let x0, y0 = int (float r.X * sx), int (float r.Y * sy)
        let x1 = int (float (r.X + r.Width) * sx) - 1
        let y1 = int (float (r.Y + r.Height) * sy) - 1
        let label = char (int '0' + (id % 10))
        for y in max 0 y0 .. min (rows - 1) y1 do
            for x in max 0 x0 .. min (cols - 1) x1 do
                grid[y, x] <- if x = x0 || x = x1 || y = y0 || y = y1 then '#' else label
    for y in 0 .. rows - 1 do
        System.String(Array.init cols (fun x -> grid[y, x])) |> printfn "    %s"

let mutable world = World.empty (Rect.create 0 0 1920 1080)

// The compositor maps some real apps (these events come from C in production).
let openApp id app =
    let w', _ = Reducer.apply (AddWindow { Id = id; AppId = app; Title = app; Floating = false }) world
    world <- w'
[ 1, "foot"; 2, "firefox"; 3, "code"; 4, "mpv" ] |> List.iter (fun (i, a) -> openApp i a)
printfn "Compositor mapped 4 windows. Workspace 1, layout=tall.\n"
render world

// The agent acts purely through JSON. Each step: it sends a command string.
let agent (intent: string) (json: string) =
    printfn "\n🤖 agent: %s" intent
    printfn "   -> %s" json
    match Protocol.parse json with
    | Some cmd ->
        let w', effects = Reducer.apply cmd world
        world <- w'
        effects
        |> List.iter (function
            | SpawnProcess p -> printfn "   [effect] compositor spawns: %s" p
            | CloseSurface id -> printfn "   [effect] compositor closes surface %d" id
            | Arrange _ -> ())
        render world
    | None -> printfn "   !! unparseable command"

agent "focus the browser by name"        """{"cmd":"focus","app":"firefox"}"""
agent "I prefer BSP tiling here"          """{"cmd":"layout","name":"bsp"}"""
agent "give the master pane more room"    """{"cmd":"ratio","value":0.65}"""
agent "move the browser to workspace 2"   """{"cmd":"workspace","move":"2"}"""
agent "open a music player"               """{"cmd":"spawn","run":"spotify"}"""

printfn "\n--- what the agent now sees (snapshot of workspace state) ---"
printfn "%s" (Protocol.snapshot world)
