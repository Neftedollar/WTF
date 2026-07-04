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

/// Force the current workspace's Layout name directly, bypassing SetLayout's
/// registry guard (mimics a hot-reloaded config writing an arbitrary string).
let private setCurrentLayout name (w: World) =
    { w with
        Workspaces =
            w.Workspaces
            |> List.map (fun ws -> if ws.Tag = w.Current then { ws with Layout = name } else ws) }

[<Fact>]
let ``FocusOrSpawn raises an existing app window, else spawns`` () =
    let w = worldWith 3   // windows 1..3 with AppIds app1..app3
    // an app that IS open -> focus its window, no spawn effect
    let w1, e1 = Reducer.apply (FocusOrSpawn("app1", "app1")) w
    Assert.Equal(Some 1, World.focusedWindow w1)
    Assert.DoesNotContain(SpawnProcess "app1", e1)
    // an app that is NOT open -> world unchanged, spawn the launch command
    let w2, e2 = Reducer.apply (FocusOrSpawn("chromium", "chromium --incognito")) w
    Assert.Equal(w, w2)
    Assert.Contains(SpawnProcess "chromium --incognito", e2)

[<Fact>]
let ``FocusOrSpawn jumps to another workspace to raise the app (no spurious spawn)`` () =
    // app1 lives on workspace "1"; we are viewing workspace "2".
    let w = worldWith 1 |> Reducer.apply (SwitchWorkspace "2") |> fst
    Assert.Equal("2", w.Current)
    // Run-or-raise must switch to "1" and focus window 1 — NOT no-op, NOT spawn a
    // second instance (the regression: a global existence check + current-stack-only
    // focus silently did nothing here).
    let w', e = Reducer.apply (FocusOrSpawn("app1", "app1")) w
    Assert.Equal("1", w'.Current)
    Assert.Equal(Some 1, World.focusedWindow w')
    Assert.DoesNotContain(SpawnProcess "app1", e)

// ---------------------------------------------------------------------------
// Spatial foundation: directional focus (InDir), SwapWith (pick-a-window),
// SwapDir (directional move). Geometry is deterministic: default tall layout,
// nmaster=1, so 2 windows tile left|right on the same row.
// ---------------------------------------------------------------------------

let private order (w: World) =
    World.currentWorkspace w |> fun ws -> ws.Stack |> Option.map Stack.toList |> Option.defaultValue []

let private swapIn a b = List.map (fun x -> if x = a then b elif x = b then a else x)

// which ids tile left / right on the current row (derived from real geometry, so
// the test doesn't depend on the stack's master-ordering).
let private leftRight (w: World) =
    let rects = World.arrange w
    (rects |> List.minBy (fun (_, r) -> int r.X) |> fst),
    (rects |> List.maxBy (fun (_, r) -> int r.X) |> fst)

[<Fact>]
let ``directional focus picks the spatial neighbour, no-op at the edge`` () =
    let w = worldWith 2                                   // two tiles, same row: left | right
    let leftId, rightId = leftRight w
    let focusOn id w = Reducer.apply (Focus(ById id)) w |> fst
    // from the left window, right -> the right window
    Assert.Equal(Some rightId, focusOn leftId w |> Reducer.apply (Focus(InDir DirRight)) |> fst |> World.focusedWindow)
    // from the right window, left -> the left window
    Assert.Equal(Some leftId, focusOn rightId w |> Reducer.apply (Focus(InDir DirLeft)) |> fst |> World.focusedWindow)
    // same row -> nothing above/below -> focus unchanged
    Assert.Equal(Some leftId, focusOn leftId w |> Reducer.apply (Focus(InDir DirUp)) |> fst |> World.focusedWindow)
    // no tile to the left of the left window -> no-op
    Assert.Equal(Some leftId, focusOn leftId w |> Reducer.apply (Focus(InDir DirLeft)) |> fst |> World.focusedWindow)

[<Fact>]
let ``SwapWith swaps the focused window with an arbitrary one, keeping focus`` () =
    let w = worldWith 3 |> fun w -> Reducer.apply (Focus(ById 1)) w |> fst
    let before = order w
    let r = Reducer.apply (SwapWith 3) w |> fst
    Assert.Equal(Some 1, World.focusedWindow r)          // focus follows the window
    Assert.Equal<int list>(before |> swapIn 1 3, order r) // 1 and 3 traded slots, rest fixed
    // swapping with the focus itself, or an absent id, is a no-op
    Assert.Equal<int list>(before, order (Reducer.apply (SwapWith 1) w |> fst))
    Assert.Equal<int list>(before, order (Reducer.apply (SwapWith 99) w |> fst))

[<Fact>]
let ``SwapDir moves the focused window to its directional neighbour, no-op at edge`` () =
    let w0 = worldWith 2
    let leftId, rightId = leftRight w0
    let w = Reducer.apply (Focus(ById leftId)) w0 |> fst  // focus the LEFT window
    let before = order w
    let r = Reducer.apply (SwapDir DirRight) w |> fst
    Assert.Equal(Some leftId, World.focusedWindow r)      // focus stays on the moved window
    Assert.Equal<int list>(before |> swapIn leftId rightId, order r)
    // no tile to the left of the left window -> unchanged
    Assert.Equal<int list>(before, order (Reducer.apply (SwapDir DirLeft) w |> fst))

[<Fact>]
let ``keybinding helpers build the expected commands`` () =
    // run-or-kill toggles by process name via a /bin/sh -c one-liner
    match runOrKill "firefox" with
    | Spawn s ->
        Assert.Contains("pgrep -x 'firefox'", s)
        Assert.Contains("pkill -x 'firefox'", s)
        Assert.Contains("setsid -f 'firefox'", s)
    | other -> failwithf "expected Spawn, got %A" other
    // explicit launch command variant
    match runOrKillCmd "kitty" "kitty --class scratch" with
    | Spawn s -> Assert.Contains("|| setsid -f kitty --class scratch", s)
    | other -> failwithf "expected Spawn, got %A" other
    // semantic helpers map to their commands
    Assert.Equal(FocusOrSpawn("firefox", "firefox"), raiseOrRun "firefox" "firefox")
    Assert.Equal(Spawn "kitty -e htop", inTerm "kitty" "htop")
    Assert.Equal(SetWallpaper "/x.jpg", setWallpaper "/x.jpg")
    // ricing / preset helpers
    Assert.Equal(CycleWallpaper [ "/a"; "/b" ], cycleWallpaper [ "/a"; "/b" ])
    Assert.Equal(Batch [ SetGaps 0; SetLayout "full" ], batch [ SetGaps 0; SetLayout "full" ])
    // OS-tool wrappers are Spawn one-liners over the standard tools
    match screenshot with Spawn s -> Assert.Contains("grim", s) | o -> failwithf "%A" o
    match screenshotArea with Spawn s -> Assert.Contains("slurp", s); Assert.Contains("wl-copy", s) | o -> failwithf "%A" o
    Assert.Equal(Spawn "loginctl lock-session", lockScreen)

[<Fact>]
let ``ricing toggles + presets are host-handled no-ops in the pure reducer`` () =
    let w = worldWith 2
    // Eye-candy toggles, wallpaper cycle, and Batch touch the renderer/host, not
    // World — the reducer leaves World untouched, emits nothing, records no undo.
    for cmd in [ ToggleBlur; ToggleWatercolor; ToggleShadows; ToggleGlow
                 CycleWallpaper [ "/a"; "/b" ]; Batch [ SetGaps 0; ToggleBlur ] ] do
        let w', effects = Reducer.apply cmd w
        Assert.Equal(w, w')
        Assert.Empty(effects)
        Assert.False(Reducer.isUndoable cmd)

[<Fact>]
let ``surface toggle commands are pure host-handled no-ops`` () =
    let w = worldWith 3
    // The reducer leaves World untouched and emits no effects (the host owns the
    // real overlay surface); and they never record an undo point.
    for cmd in [ ToggleOmnibox; ToggleOverlay "omnibox"; ToggleOverlay "spotlight" ] do
        let w', effects = Reducer.apply cmd w
        Assert.Equal(w, w')
        Assert.Empty(effects)
        Assert.False(Reducer.isUndoable cmd)

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

[<Fact>]
let ``undo redo and session commands are pure reducer no-ops`` () =
    let w = worldWith 2
    for cmd in [ Undo; Redo; SaveSession; LoadSession ] do
        let w', e = Reducer.apply cmd w
        Assert.Equal<World>(w, w')
        Assert.Empty(e)

[<Fact>]
let ``isUndoable allows reversible world mutations and excludes the rest`` () =
    let undoable =
        [ Focus Focused; FocusMaster; SwapNext; SwapPrev; SwapMaster
          SwitchWorkspace "2"; MoveToWorkspace "2"; NextWorkspace; PrevWorkspace
          SetLayout "bsp"; NextLayout; SetMaster 2; IncMaster; DecMaster
          SetRatio 0.6; SetGaps 4; IncGaps; DecGaps ]
    let notUndoable =
        [ CloseFocused; Spawn "x"
          SetInactiveOpacity 0.5; SetAnimationSpeed 0.3; SetBorderWidth 2
          SetBorderColor(true, 0.0, 0.0, 0.0); SetCornerRadius 4; SetBlur true
          Undo; Redo; SaveSession; LoadSession
          AddWindow(win 9 "z"); RemoveWindow 9 ]
    Assert.All(undoable, fun c -> Assert.True(Reducer.isUndoable c, sprintf "%A should be undoable" c))
    Assert.All(notUndoable, fun c -> Assert.False(Reducer.isUndoable c, sprintf "%A should not be undoable" c))

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

// =====================================================================
//  Coverage additions: focus preservation on removal, stack-uniqueness,
//  MoveToWorkspace boundaries, clamp behaviour on every numeric/render
//  knob, effect-only commands, invalid-tag no-ops, and the arrange
//  fallback for an unresolvable layout. (Reviewer "core-world-reducer".)
// =====================================================================

let private curStack w =
    (World.currentWorkspace w).Stack |> Option.get |> Stack.toList

// ---- RemoveWindow: focus preservation (regression for focus-steal) --

[<Fact>]
let ``removing a non-focused window preserves the current focus`` () =
    let w = worldWith 3 // stack [3;2;1], focus = 3
    Assert.Equal(Some 3, World.focusedWindow w)
    let w', _ = Reducer.apply (RemoveWindow 1) w // 1 is NOT focused
    Assert.Equal<int list>([ 3; 2 ], curStack w')
    Assert.Equal(Some 3, World.focusedWindow w') // focus stays on 3, not stolen to 2

[<Fact>]
let ``removing the focused window shifts focus down then up`` () =
    let w = worldWith 3 // stack [3;2;1], focus = 3 (top, no down-from-3? Down=[2;1])
    let w', _ = Reducer.apply (RemoveWindow 3) w // remove the focused top window
    Assert.Equal<int list>([ 2; 1 ], curStack w')
    Assert.Equal(Some 2, World.focusedWindow w') // delete picks the down neighbour

[<Fact>]
let ``removing the focused bottom window falls back to the up neighbour`` () =
    let w = worldWith 3 |> Reducer.apply (Focus(ById 1)) |> fst // focus = 1 (bottom)
    let w', _ = Reducer.apply (RemoveWindow 1) w
    Assert.Equal<int list>([ 3; 2 ], curStack w')
    Assert.Equal(Some 2, World.focusedWindow w') // no Down -> up neighbour

// ---- AddWindow: stack-uniqueness (regression for duplicate insert) --

[<Fact>]
let ``re-adding an existing id keeps it in the stack exactly once`` () =
    let w = worldWith 2 // [2;1]
    let w', _ = Reducer.apply (AddWindow { Id = 1; AppId = "renamed"; Title = "t2"; Floating = false }) w
    let ids = curStack w'
    Assert.Equal(List.length (List.distinct ids), List.length ids) // no duplicate id
    Assert.True(List.contains 1 ids)
    Assert.Equal("renamed", (Map.find 1 w'.Windows).AppId) // Windows holds the latest info

// ---- MoveToWorkspace boundaries -------------------------------------

[<Fact>]
let ``move to the same workspace is a no-op`` () =
    let w = worldWith 2
    let w', e = Reducer.apply (MoveToWorkspace "1") w // current is "1"
    Assert.Equal<World>(w, w')
    Assert.Empty(e)

[<Fact>]
let ``move to a non-existent workspace is a no-op`` () =
    let w = worldWith 2
    let w', e = Reducer.apply (MoveToWorkspace "does-not-exist") w
    Assert.Equal<World>(w, w')
    Assert.Empty(e)

[<Fact>]
let ``move from an empty workspace is a no-op`` () =
    let w = World.empty screen // no windows, no focus
    let w', e = Reducer.apply (MoveToWorkspace "2") w
    Assert.Equal<World>(w, w')
    Assert.Empty(e)

[<Fact>]
let ``moving the only window empties the source workspace stack`` () =
    let w = worldWith 1
    let w', _ = Reducer.apply (MoveToWorkspace "2") w
    Assert.Equal(None, (w'.Workspaces |> List.find (fun ws -> ws.Tag = "1")).Stack)
    Assert.Equal<int list>([ 1 ], World.stackOf "2" w' |> Option.get |> Stack.toList)

[<Fact>]
let ``moving into a non-empty target inserts and focuses the moved id`` () =
    // put window 9 on "2" first, then move focused 2 from "1" to "2"
    let w =
        worldWith 2 // [2;1] on "1", focus 2
        |> Reducer.apply (SwitchWorkspace "2") |> fst
        |> Reducer.apply (AddWindow(win 9 "nine")) |> fst // "2" has [9]
        |> Reducer.apply (SwitchWorkspace "1") |> fst
    let w', _ = Reducer.apply (MoveToWorkspace "2") w // moves focused 2
    let tgt = World.stackOf "2" w' |> Option.get
    Assert.Equal<int list>([ 2; 9 ], Stack.toList tgt) // insertUp => moved on top
    Assert.Equal(2, tgt.Focus) // target focuses the moved id

// ---- numeric clamp commands -----------------------------------------

[<Fact>]
let ``set ratio clamps to the 0.1..0.9 band`` () =
    let w = worldWith 1
    Assert.Equal(0.9, (Reducer.apply (SetRatio 5.0) w |> fst).Ratio)
    Assert.Equal(0.1, (Reducer.apply (SetRatio -1.0) w |> fst).Ratio)

[<Fact>]
let ``set master clamps zero and negatives to one`` () =
    let w = worldWith 1
    Assert.Equal(1, (Reducer.apply (SetMaster 0) w |> fst).Nmaster)
    Assert.Equal(1, (Reducer.apply (SetMaster -5) w |> fst).Nmaster)
    Assert.Equal(3, (Reducer.apply (SetMaster 3) w |> fst).Nmaster)

[<Fact>]
let ``set gaps floors negatives at zero and inc adds four`` () =
    let w = worldWith 1
    Assert.Equal(0, (Reducer.apply (SetGaps -10) w |> fst).Gaps)
    let g0 = (Reducer.apply (SetGaps 5) w |> fst)
    Assert.Equal(9, (Reducer.apply IncGaps g0 |> fst).Gaps)

// ---- render-knob clamps ---------------------------------------------

[<Fact>]
let ``inactive opacity clamps to 0..1`` () =
    let w = worldWith 1
    Assert.Equal<Effect list>([ RenderOpacity 1.0 ], Reducer.apply (SetInactiveOpacity 2.0) w |> snd)
    Assert.Equal<Effect list>([ RenderOpacity 0.0 ], Reducer.apply (SetInactiveOpacity -0.5) w |> snd)

[<Fact>]
let ``animation speed clamps to a 0.01..1 band`` () =
    let w = worldWith 1
    Assert.Equal<Effect list>([ RenderAnimSpeed 0.01 ], Reducer.apply (SetAnimationSpeed 0.0) w |> snd)
    Assert.Equal<Effect list>([ RenderAnimSpeed 1.0 ], Reducer.apply (SetAnimationSpeed 5.0) w |> snd)

[<Fact>]
let ``border width and corner radius floor negatives at zero`` () =
    let w = worldWith 1
    Assert.Equal<Effect list>([ RenderBorderWidth 0 ], Reducer.apply (SetBorderWidth -3) w |> snd)
    Assert.Equal<Effect list>([ RenderCornerRadius 0 ], Reducer.apply (SetCornerRadius -7) w |> snd)

[<Fact>]
let ``border color passes through and blur toggles both ways`` () =
    let w = worldWith 1
    Assert.Equal<Effect list>(
        [ RenderBorderColor(true, 0.2, 0.4, 0.6) ],
        Reducer.apply (SetBorderColor(true, 0.2, 0.4, 0.6)) w |> snd)
    Assert.Equal<Effect list>([ RenderBlur true ], Reducer.apply (SetBlur true) w |> snd)
    Assert.Equal<Effect list>([ RenderBlur false ], Reducer.apply (SetBlur false) w |> snd)

// ---- effect-only commands: CloseFocused / Spawn ---------------------

[<Fact>]
let ``close focused emits exactly one CloseSurface for the focused id`` () =
    let w = worldWith 3 // focus = 3
    let w', e = Reducer.apply CloseFocused w
    Assert.Equal<World>(w, w') // no World change
    Assert.Equal<Effect list>([ CloseSurface 3 ], e)

[<Fact>]
let ``close focused on an empty workspace emits nothing`` () =
    let w = World.empty screen
    let w', e = Reducer.apply CloseFocused w
    Assert.Equal<World>(w, w')
    Assert.Empty(e)

[<Fact>]
let ``spawn emits a SpawnProcess and leaves the world unchanged`` () =
    let w = worldWith 1
    let w', e = Reducer.apply (Spawn "foot") w
    Assert.Equal<World>(w, w')
    Assert.Equal<Effect list>([ SpawnProcess "foot" ], e)

[<Fact>]
let ``spawnOnce emits a SpawnProcessOnce and leaves the world unchanged`` () =
    let w = worldWith 1
    let w', e = Reducer.apply (SpawnOnce "wtf-omnibox") w
    Assert.Equal<World>(w, w')
    Assert.Equal<Effect list>([ SpawnProcessOnce "wtf-omnibox" ], e)

[<Fact>]
let ``once wraps a Spawn into a SpawnOnce, passes other commands through`` () =
    Assert.Equal<Command>(SpawnOnce "wtf-omnibox", once (Spawn "wtf-omnibox"))
    // non-Spawn commands are returned unchanged
    Assert.Equal<Command>(CloseFocused, once CloseFocused)
    Assert.Equal<Command>(Focus NextWindow, once (Focus NextWindow))

[<Fact>]
let ``spawnOnce is not undoable`` () =
    Assert.False(Reducer.isUndoable (SpawnOnce "x"))

// ---- SwitchWorkspace invalid / idempotent ---------------------------

[<Fact>]
let ``switch to a non-existent workspace is a no-op`` () =
    let w = worldWith 1
    let w', e = Reducer.apply (SwitchWorkspace "nope") w
    Assert.Equal<World>(w, w')
    Assert.Empty(e)

[<Fact>]
let ``switch to the current workspace keeps Current unchanged`` () =
    let w = worldWith 1
    let w', _ = Reducer.apply (SwitchWorkspace "1") w
    Assert.Equal("1", w'.Current)

// ---- workspace wrap (Next direction) + NextLayout fallback ----------

[<Fact>]
let ``next workspace from 9 wraps back to 1`` () =
    let w = { worldWith 1 with Current = "9" }
    let w', _ = Reducer.apply NextWorkspace w
    Assert.Equal("1", w'.Current)

[<Fact>]
let ``next layout from an unknown current layout falls to the first registered`` () =
    let w = setCurrentLayout "garbage" (worldWith 1)
    let w', _ = Reducer.apply NextLayout w
    Assert.Equal(List.head (Registry.names ()), (World.currentWorkspace w').Layout)

// ---- Focus selectors: wrap + misses ---------------------------------

[<Fact>]
let ``focus next and prev wrap at the stack ends`` () =
    let w = worldWith 3 |> Reducer.apply (Focus(ById 1)) |> fst // focus = 1 (bottom)
    let down, _ = Reducer.apply (Focus NextWindow) w // wraps to top
    Assert.Equal(Some 3, World.focusedWindow down)
    let up, _ = Reducer.apply (Focus PrevWindow) (worldWith 3) // focus 3 (top) wraps up to bottom
    Assert.Equal(Some 1, World.focusedWindow up)

[<Fact>]
let ``focus by absent id or app is a no-op on the current focus`` () =
    let w = worldWith 3 // focus = 3
    Assert.Equal(Some 3, World.focusedWindow (Reducer.apply (Focus(ById 999)) w |> fst))
    Assert.Equal(Some 3, World.focusedWindow (Reducer.apply (Focus(ByApp "nope")) w |> fst))

// ---- ToggleFloat / ToggleFullscreen on an empty workspace -----------

[<Fact>]
let ``toggle float and fullscreen on an empty workspace are no-ops`` () =
    let w = World.empty screen
    let wf, ef = Reducer.apply ToggleFloat w
    Assert.Equal<World>(w, wf)
    Assert.Empty(ef)
    let wfs, efs = Reducer.apply ToggleFullscreen w
    Assert.Equal<World>(w, wfs)
    Assert.Empty(efs)

// ---- arrange fallback for an unresolvable layout (regression) -------

[<Fact>]
let ``arrange still rects every tiled window under an unresolvable layout`` () =
    let w = setCurrentLayout "not-a-real-layout" (worldWith 3)
    let arrangeIds = World.arrange w |> List.map fst |> List.sort
    let stackIds = curStack w |> List.sort
    Assert.Equal<int list>(stackIds, arrangeIds) // no tiled window vanishes
