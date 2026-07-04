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
let ``workspace Type and per-type State round-trip (#5)`` () =
    // Non-default Type + State must survive save/restore (a stateful plugin type's
    // data is persisted). Set them directly (session doesn't validate type names).
    let w =
        worldWith 2
        |> World.setTypeOf "1" "paperwm"
        |> World.setStateOf "1" "viewport:120"
    match Session.ofJson (Session.toJson w) with
    | Some w' ->
        let ws = World.currentWorkspace w'
        Assert.Equal("paperwm", ws.Type)
        Assert.Equal("viewport:120", ws.State)
        Assert.Equal(Some w, Some w')
    | None -> failwith "should round-trip"

[<Fact>]
let ``a pre-#5 session without type/state loads with defaults`` () =
    // Backward compat: a workspace object missing "type"/"state" must default to
    // "stack"/"" rather than fail the whole parse.
    let full = Session.toJson (World.empty screen)
    // Drop the key:value tokens (trailing commas kept the objects valid); the
    // leftover indentation is just whitespace the JSON parser ignores.
    let stripped = full.Replace("\"type\": \"stack\",", "").Replace("\"state\": \"\",", "")
    match Session.ofJson stripped with
    | Some w ->
        let ws = World.currentWorkspace w
        Assert.Equal("stack", ws.Type)
        Assert.Equal("", ws.State)
    | None -> failwith "a pre-#5 session should still load"

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

// ============================================================================
//  Floating + Fullscreen codec round-trips (the most explicitly requested gap:
//  the existing tests never exercise a non-empty Floating map or Fullscreen).
// ============================================================================

[<Fact>]
let ``a floating + fullscreen world round-trips losslessly`` () =
    let w =
        Reducer.applyMany
            [ AddWindow(win 1 "a"); AddWindow(win 2 "b"); AddWindow(win 3 "c")
              Focus(ById 2); ToggleFloat          // 2 floats (real stored rect)
              Focus(ById 3); ToggleFullscreen ]   // 3 fullscreen
            (World.empty screen)
        |> fst
    let ws = World.currentWorkspace w
    Assert.False(Map.isEmpty ws.Floating)         // precondition: actually floating
    Assert.Equal(Some 3, ws.Fullscreen)
    Assert.Equal(Some w, Session.ofJson (Session.toJson w))

[<Fact>]
let ``multiple floating windows round-trip`` () =
    let w =
        Reducer.applyMany
            [ AddWindow(win 1 "a"); AddWindow(win 2 "b"); AddWindow(win 3 "c")
              Focus(ById 1); ToggleFloat
              Focus(ById 3); ToggleFloat ]
            (World.empty screen)
        |> fst
    Assert.Equal(2, (World.currentWorkspace w).Floating.Count)
    Assert.Equal(Some w, Session.ofJson (Session.toJson w))

[<Fact>]
let ``a window that is both floating and fullscreen round-trips`` () =
    let w =
        Reducer.applyMany
            [ AddWindow(win 1 "a"); AddWindow(win 2 "b")
              Focus(ById 2); ToggleFloat; ToggleFullscreen ]
            (World.empty screen)
        |> fst
    let ws = World.currentWorkspace w
    Assert.True(Map.containsKey 2 ws.Floating)
    Assert.Equal(Some 2, ws.Fullscreen)
    Assert.Equal(Some w, Session.ofJson (Session.toJson w))

// ============================================================================
//  ofJson fail-closed contract: a structurally valid header but missing/invalid
//  required content must yield None, never a partial World.
// ============================================================================

[<Fact>]
let ``valid header but missing required keys returns None`` () =
    Assert.Equal(None, Session.ofJson """{"schema":"wtf-session","version":1}""")

[<Fact>]
let ``a workspace missing tag or layout returns None`` () =
    let json =
        """{"schema":"wtf-session","version":1,"current":"1","nmaster":1,"ratio":0.5,"gaps":6,
            "screen":{"x":0,"y":0,"w":100,"h":100},
            "workspaces":[{"layout":"tall","stack":null,"floating":[],"fullscreen":null}],
            "windows":[]}"""
    Assert.Equal(None, Session.ofJson json)

[<Fact>]
let ``version as a string returns None`` () =
    Assert.Equal(None, Session.ofJson """{"schema":"wtf-session","version":"1"}""")

[<Fact>]
let ``schema without version (and vice versa) returns None`` () =
    Assert.Equal(None, Session.ofJson """{"schema":"wtf-session"}""")
    Assert.Equal(None, Session.ofJson """{"version":1}""")

[<Fact>]
let ``a corrupt focused id (not in the stack windows) fails closed`` () =
    // Regression: this used to load with the focus silently reset to the head.
    let w =
        Reducer.applyMany [ AddWindow(win 1 "a"); AddWindow(win 2 "b") ] (World.empty screen)
        |> fst
    let corrupt = (Session.toJson w).Replace("\"focused\": 2", "\"focused\": 99")
    Assert.NotEqual<string>(Session.toJson w, corrupt) // the substitution actually happened
    Assert.Equal(None, Session.ofJson corrupt)
