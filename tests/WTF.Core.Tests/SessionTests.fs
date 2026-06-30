module WTF.Core.Tests.SessionTests

open Xunit
open FsCheck.Xunit
open WTF.Core

let private screen = Rect.create 0 0 1920 1080
let private win id app = { Id = id; AppId = app; Title = app; Floating = false }

let private worldWith n =
    [ for i in 1..n -> AddWindow(win i (sprintf "app%d" i)) ]
    |> fun cmds -> Reducer.applyMany cmds (World.empty screen) |> fst

[<Property>]
let ``session round-trips for arbitrary window counts`` (k: int) =
    let n = (abs k % 6) + 1
    let w = worldWith n
    Session.ofJson (Session.toJson w) = Some w

[<Fact>]
let ``session round-trips after a sequence of mutations`` () =
    let w = worldWith 4
    let w2, _ =
        Reducer.applyMany
            [ Focus(ById 2); SwapMaster; SetLayout "bsp"; MoveToWorkspace "3"; SwitchWorkspace "3"; SetRatio 0.7; IncMaster ]
            w
    Assert.Equal(Some w2, Session.ofJson (Session.toJson w2))

[<Fact>]
let ``empty workspaces round-trip with null stacks`` () =
    let w = World.empty screen // every workspace Stack = None
    Assert.Equal(Some w, Session.ofJson (Session.toJson w))

[<Fact>]
let ``the Windows map round-trips`` () =
    let w = worldWith 3
    match Session.ofJson (Session.toJson w) with
    | Some w' -> Assert.Equal<Map<WindowId, WindowInfo>>(w.Windows, w'.Windows)
    | None -> failwith "expected Some"

[<Fact>]
let ``ratio round-trips exactly under invariant culture`` () =
    let w = { World.empty screen with Ratio = 0.5 }
    match Session.ofJson (Session.toJson w) with
    | Some w' -> Assert.Equal(0.5, w'.Ratio)
    | None -> failwith "expected Some"

[<Fact>]
let ``the focused window survives the zipper round-trip`` () =
    let w = worldWith 4
    let w1, _ = Reducer.apply (Focus(ById 2)) w
    match Session.ofJson (Session.toJson w1) with
    | Some w' -> Assert.Equal(Some 2, World.focusedWindow w')
    | None -> failwith "expected Some"

[<Fact>]
let ``garbage returns None`` () =
    Assert.Equal(None, Session.ofJson "not json")
    Assert.Equal(None, Session.ofJson "{}")

[<Fact>]
let ``version mismatch returns None`` () =
    Assert.Equal(None, Session.ofJson """{"schema":"wtf-session","version":99}""")
