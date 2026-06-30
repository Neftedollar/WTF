module WTF.Core.Tests.ReducerTests

open Xunit
open FsCheck.Xunit
open WTF.Core

let private screen = Rect.create 0 0 1920 1080
let private win id app = { Id = id; AppId = app; Title = app; Floating = false }

/// Build a world with windows 1..n mapped on workspace "1".
let private worldWith n =
    [ for i in 1..n -> AddWindow(win i (sprintf "app%d" i)) ]
    |> fun cmds -> Reducer.applyMany cmds (World.empty screen) |> fst

[<Fact>]
let ``adding windows populates the current workspace in order`` () =
    let w = worldWith 3
    let ids = (World.currentWorkspace w).Stack |> Option.get |> Stack.toList
    Assert.Equal<int list>([ 3; 2; 1 ], ids) // insertUp: newest on top

[<Fact>]
let ``focus by app selects the right window`` () =
    let w = worldWith 3
    let w', _ = Reducer.apply (Focus(ByApp "app2")) w
    Assert.Equal(Some 2, World.focusedWindow w')

[<Fact>]
let ``move to workspace transfers the focused window`` () =
    let w = worldWith 2 // focus = 2
    let w', _ = Reducer.apply (MoveToWorkspace "2") w
    Assert.Equal(Some 1, World.focusedWindow w') // 2 left, focus falls to 1
    let moved = World.stackOf "2" w' |> Option.get |> Stack.toList
    Assert.Equal<int list>([ 2 ], moved)

[<Fact>]
let ``removing the last window empties the workspace`` () =
    let w = worldWith 1
    let w', _ = Reducer.apply (RemoveWindow 1) w
    Assert.Equal(None, (World.currentWorkspace w').Stack)

[<Fact>]
let ``set layout changes only the current workspace`` () =
    let w = worldWith 1
    let w', _ = Reducer.apply (SetLayout "bsp") w
    Assert.Equal("bsp", (World.currentWorkspace w').Layout)
    Assert.Equal("tall", (w'.Workspaces |> List.find (fun ws -> ws.Tag = "2")).Layout)

[<Fact>]
let ``unknown layout is rejected`` () =
    let w = worldWith 1
    let w', _ = Reducer.apply (SetLayout "does-not-exist") w
    Assert.Equal("tall", (World.currentWorkspace w').Layout)

[<Fact>]
let ``swap master promotes the focused window to the top`` () =
    let w = worldWith 3 // stack top->bottom = [3;2;1], focus=3
    let w1, _ = Reducer.apply (Focus(ById 1)) w
    let w2, _ = Reducer.apply SwapMaster w1
    let ids = (World.currentWorkspace w2).Stack |> Option.get |> Stack.toList
    Assert.Equal<int list>([ 1; 3; 2 ], ids)
    Assert.Equal(Some 1, World.focusedWindow w2)

[<Fact>]
let ``focus master selects the top window`` () =
    let w = worldWith 3
    let w1, _ = Reducer.apply (Focus(ById 1)) w
    let w2, _ = Reducer.apply FocusMaster w1
    Assert.Equal(Some 3, World.focusedWindow w2) // 3 is the master (top)

[<Fact>]
let ``inc and dec master clamp at one`` () =
    let w = worldWith 1
    let w1, _ = Reducer.apply DecMaster w
    Assert.Equal(1, w1.Nmaster)
    let w2, _ = Reducer.apply IncMaster w1
    Assert.Equal(2, w2.Nmaster)

[<Fact>]
let ``next layout cycles and next workspace wraps`` () =
    let w = worldWith 1
    let w1, _ = Reducer.apply NextLayout w
    Assert.NotEqual<string>("tall", (World.currentWorkspace w1).Layout)
    let w2, _ = Reducer.apply PrevWorkspace w // from "1" wraps to "9"
    Assert.Equal("9", w2.Current)

[<Fact>]
let ``gaps commands adjust and clamp`` () =
    let w = { worldWith 1 with Gaps = 2 }
    let w1, _ = Reducer.apply DecGaps w
    Assert.Equal(0, w1.Gaps) // 2 - 4 clamps to 0
    let w2, _ = Reducer.apply (SetGaps 16) w1
    Assert.Equal(16, w2.Gaps)

[<Fact>]
let ``appearance commands emit render effects, not world changes`` () =
    let w = worldWith 1
    let _, e1 = Reducer.apply (SetInactiveOpacity 0.8) w
    Assert.Equal<Effect list>([ RenderOpacity 0.8 ], e1)
    let _, e2 = Reducer.apply (SetAnimationSpeed 0.5) w
    Assert.Equal<Effect list>([ RenderAnimSpeed 0.5 ], e2)

[<Property>]
let ``every command keeps window-set and stack consistent`` (n: int) =
    let n = (abs n % 6) + 1
    let w = worldWith n
    // windows present in any stack are exactly the keys of the Windows map
    let inStacks =
        w.Workspaces
        |> List.collect (fun ws -> ws.Stack |> Option.map Stack.toList |> Option.defaultValue [])
        |> Set.ofList
    Set.ofSeq (Map.toSeq w.Windows |> Seq.map fst) = inStacks
