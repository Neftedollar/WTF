module WTF.Core.Tests.FloatingTests

open Xunit
open FsCheck.Xunit
open WTF.Core

// =====================================================================
//  Floating + Fullscreen + Stacking (Phase 1 #5). Pins the 3-layer
//  arrange (ascending z: tiled -> floating -> fullscreen), the new
//  ToggleFloat / ToggleFullscreen / SinkAll commands + SetFullscreen
//  effect, the WindowInfo.Floating mirror, removal/move purges, and the
//  Session round-trip of the new fields.
// =====================================================================

let private screen = Rect.create 0 0 1920 1080
let private win id app : WindowInfo = { Id = id; AppId = app; Title = app; Floating = false }

let private worldWith n =
    [ for i in 1..n -> AddWindow(win i (sprintf "app%d" i)) ]
    |> fun cmds -> Reducer.applyMany cmds (World.empty screen) |> fst

/// A 5-window world on "1" with 2 & 4 floating and 1 fullscreen.
/// Stack order (top->bottom) is [5;4;3;2;1]; tiled = {5;3}.
let private mixed () =
    let step cmd w = Reducer.apply cmd w |> fst
    worldWith 5
    |> step (Focus(ById 2)) |> step ToggleFloat
    |> step (Focus(ById 4)) |> step ToggleFloat
    |> step (Focus(ById 1)) |> step ToggleFullscreen

// ---- (1) toggle involutions -----------------------------------------

[<Property>]
let ``toggle float twice is identity from a tiled window`` (k: int) =
    let n = (abs k % 6) + 1
    let w = worldWith n
    let w1, _ = Reducer.apply ToggleFloat w
    let w2, _ = Reducer.apply ToggleFloat w1
    w2 = w

[<Property>]
let ``toggle fullscreen twice is identity with balanced effects`` (k: int) =
    let n = (abs k % 6) + 1
    let w = worldWith n
    let id = (World.focusedWindow w).Value
    let w1, e1 = Reducer.apply ToggleFullscreen w
    let w2, e2 = Reducer.apply ToggleFullscreen w1
    w2 = w
    && List.contains (SetFullscreen(id, true)) e1
    && List.contains (SetFullscreen(id, false)) e2

[<Fact>]
let ``replacing the fullscreen window clears the old one first`` () =
    let w = worldWith 3 // focus = 3
    let w1, _ = Reducer.apply ToggleFullscreen w // 3 fullscreen
    let w2, e2 = Reducer.apply (Focus(ById 1)) w1 |> fst |> Reducer.apply ToggleFullscreen
    ignore w2
    Assert.True(List.contains (SetFullscreen(3, false)) e2)
    Assert.True(List.contains (SetFullscreen(1, true)) e2)

[<Fact>]
let ``moving a fullscreen window to another workspace clears its surface flag`` () =
    let w = worldWith 3 // focus = 3
    let w1, _ = Reducer.apply ToggleFullscreen w // 3 fullscreen on ws "1"
    let w2, e2 = Reducer.apply (MoveToWorkspace "2") w1
    Assert.True(List.contains (SetFullscreen(3, false)) e2) // surface flag cleared
    Assert.Equal(None, (w2.Workspaces |> List.find (fun ws -> ws.Tag = "1")).Fullscreen)
    Assert.Equal(None, (w2.Workspaces |> List.find (fun ws -> ws.Tag = "2")).Fullscreen)

// ---- (2) focus preservation -----------------------------------------

[<Property>]
let ``toggling float and fullscreen never changes the focused id`` (k: int) =
    let n = (abs k % 6) + 1
    let w = worldWith n
    let f0 = World.focusedWindow w
    let w1, _ = Reducer.apply ToggleFloat w
    let w2, _ = Reducer.apply ToggleFullscreen w1
    World.focusedWindow w1 = f0 && World.focusedWindow w2 = f0

// ---- (3) arrange: no window lost (bijection) ------------------------

[<Fact>]
let ``arrange ids are a bijection of the current stack`` () =
    let w = mixed ()
    let arrangeIds = World.arrange w |> List.map fst
    let stackIds = (World.currentWorkspace w).Stack |> Option.get |> Stack.toList
    Assert.Equal<int list>(List.sort stackIds, List.sort arrangeIds)
    Assert.Equal(arrangeIds.Length, (List.distinct arrangeIds).Length) // no duplicates

// ---- (4) partition: tiled / floating / fullscreen -------------------

[<Fact>]
let ``tiled floating and fullscreen partition the stacked ids`` () =
    let w = mixed ()
    let ws = World.currentWorkspace w
    let all = ws.Stack |> Option.get |> Stack.toList |> Set.ofList
    let floating =
        ws.Floating |> Map.toList |> List.map fst |> List.filter (fun i -> Set.contains i all) |> Set.ofList
    let fs = match ws.Fullscreen with Some i when Set.contains i all -> Set.singleton i | _ -> Set.empty
    let tiled = all - floating - fs
    Assert.True(Set.isEmpty (Set.intersect floating fs))
    Assert.True(Set.isEmpty (Set.intersect floating tiled))
    Assert.True(Set.isEmpty (Set.intersect tiled fs))
    Assert.Equal<Set<int>>(all, Set.unionMany [ tiled; floating; fs ])

// ---- (5) z-order layering: tiled < floating < fullscreen ------------

[<Fact>]
let ``arrange is ascending z: tiled then floating then fullscreen`` () =
    let w = mixed ()
    let ws = World.currentWorkspace w
    let arr = World.arrange w |> List.map fst
    let idxOf id = List.findIndex ((=) id) arr
    let floating = ws.Floating |> Map.toList |> List.map fst
    let tiled = arr |> List.filter (fun id -> not (List.contains id floating) && ws.Fullscreen <> Some id)
    let fsIdx = idxOf (ws.Fullscreen |> Option.get)
    for t in tiled do
        for f in floating do
            Assert.True(idxOf t < idxOf f, sprintf "tiled %d should be below floating %d" t f)
    for f in floating do
        Assert.True(idxOf f < fsIdx, sprintf "floating %d should be below fullscreen" f)

// ---- (6) fullscreen covers the screen and is on top -----------------

[<Fact>]
let ``the fullscreen window covers the screen and is the last entry`` () =
    let w = mixed ()
    let arr = World.arrange w
    let id = (World.currentWorkspace w).Fullscreen |> Option.get
    Assert.Equal((id, screen), List.last arr)

// ---- (7) mirror consistency -----------------------------------------

let private ownsId id (ws: Workspace) =
    match ws.Stack with Some s -> List.contains id (Stack.toList s) | None -> false

/// WindowInfo.Floating = true  iff  id is a key of its owning workspace's Floating map.
let private mirrorConsistent (w: World) =
    w.Windows
    |> Map.forall (fun id info ->
        match w.Workspaces |> List.tryFind (ownsId id) with
        | Some ws -> info.Floating = Map.containsKey id ws.Floating
        | None -> not info.Floating) // an orphaned window must be non-floating

[<Fact>]
let ``the floating mirror stays consistent across every mutation`` () =
    let w = mixed ()
    Assert.True(mirrorConsistent w, "after float/fullscreen toggles")
    let w2, _ = Reducer.apply (MoveToWorkspace "2") w // moves focused (1), sinking it
    Assert.True(mirrorConsistent w2, "after MoveToWorkspace")
    let w3, _ = Reducer.apply (RemoveWindow 4) w2
    Assert.True(mirrorConsistent w3, "after RemoveWindow")
    let w4, _ = Reducer.apply ToggleFloat w3
    Assert.True(mirrorConsistent w4, "after ToggleFloat")
    let w5, _ = Reducer.apply SinkAll w4
    Assert.True(mirrorConsistent w5, "after SinkAll")

[<Fact>]
let ``the FloatWindow manage rule floats the new window with a default rect`` () =
    let cfg = config { manageHook (manage { rule anyWindow FloatWindow }) }
    let w, _ = Manage.onAdd cfg (win 1 "foo") (World.empty screen)
    let ws = World.currentWorkspace w
    Assert.True(Map.containsKey 1 ws.Floating)
    Assert.True((Map.find 1 w.Windows).Floating)
    Assert.True(mirrorConsistent w)
    Assert.Contains((1, World.clampFloat screen (World.defaultFloatRect screen)), World.arrange w)

// ---- (8) floating rects stay on-screen; clampFloat idempotent -------

[<Property>]
let ``clampFloat is idempotent`` (x: int) (y: int) (w: int) (h: int) =
    let r = Rect.create x y w h
    World.clampFloat screen (World.clampFloat screen r) = World.clampFloat screen r

[<Fact>]
let ``clampFloat pulls an off-screen rect back on-screen`` () =
    let c = World.clampFloat screen (Rect.create 5000 5000 4000 4000)
    Assert.True(c.X >= screen.X && c.Y >= screen.Y)
    Assert.True(c.X + c.Width <= screen.X + screen.Width)
    Assert.True(c.Y + c.Height <= screen.Y + screen.Height)

[<Fact>]
let ``every floating rect in arrange is within screen bounds`` () =
    let w = mixed ()
    let ws = World.currentWorkspace w
    let floating = ws.Floating |> Map.toList |> List.map fst
    for (id, r) in World.arrange w do
        if List.contains id floating && ws.Fullscreen <> Some id then
            Assert.True(r.X >= screen.X && r.Y >= screen.Y)
            Assert.True(r.X + r.Width <= screen.X + screen.Width)
            Assert.True(r.Y + r.Height <= screen.Y + screen.Height)

// ---- (9) removal safety: no dangling references ---------------------

[<Fact>]
let ``removing a window purges all floating and fullscreen references`` () =
    let w = mixed () // 2 & 4 floating, 1 fullscreen
    let w1, _ = Reducer.apply (RemoveWindow 4) w
    let w2, _ = Reducer.apply (RemoveWindow 1) w1
    for ws in w2.Workspaces do
        Assert.False(Map.containsKey 4 ws.Floating)
        Assert.False(Map.containsKey 1 ws.Floating)
        Assert.NotEqual(Some 1, ws.Fullscreen)
    // arrange never references a non-stacked id
    let stackIds = (World.currentWorkspace w2).Stack |> Option.map Stack.toList |> Option.defaultValue []
    for (id, _) in World.arrange w2 do
        Assert.Contains(id, stackIds)

// ---- (10) session round-trips the new fields ------------------------

[<Fact>]
let ``session round-trips non-empty floating and fullscreen`` () =
    let w = mixed ()
    Assert.Equal(Some w, Session.ofJson (Session.toJson w))

// ---- (11) protocol + undoability wiring -----------------------------

[<Fact>]
let ``float fullscreen and sinkall commands parse`` () =
    Assert.Equal(Some ToggleFloat, Protocol.parse """{"cmd":"float"}""")
    Assert.Equal(Some ToggleFullscreen, Protocol.parse """{"cmd":"fullscreen"}""")
    Assert.Equal(Some SinkAll, Protocol.parse """{"cmd":"sinkall"}""")

[<Fact>]
let ``the snapshot exposes floating members and the fullscreen id`` () =
    let w = mixed ()
    let doc = System.Text.Json.JsonDocument.Parse(Protocol.snapshot w)
    let ws1 = doc.RootElement.GetProperty("workspaces").EnumerateArray() |> Seq.head
    Assert.Equal(2, ws1.GetProperty("floating").GetArrayLength())
    Assert.Equal(1, ws1.GetProperty("fullscreen").GetInt32())

[<Fact>]
let ``toggle float fullscreen and sinkall are undoable`` () =
    Assert.True(Reducer.isUndoable ToggleFloat)
    Assert.True(Reducer.isUndoable ToggleFullscreen)
    Assert.True(Reducer.isUndoable SinkAll)

// ---- (12) z-order WITHIN the floating layer (documented: stack=z) ---

[<Fact>]
let ``within the floating layer z follows stack order`` () =
    // mixed(): stack top->bottom [5;4;3;2;1], floats = {4;2}. arrange visits ids
    // in stack order, so the higher-stacked float (4) precedes the lower (2):
    // later in the arrange list = higher z. This pins the documented behaviour.
    let w = mixed ()
    let arr = World.arrange w |> List.map fst
    let idxOf id = List.findIndex ((=) id) arr
    Assert.True(idxOf 4 < idxOf 2, "stack order among floats must be preserved (4 above 2 in stack => lower z)")
