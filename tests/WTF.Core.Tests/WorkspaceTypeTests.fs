// These tests MUTATE the process-global WorkspaceRegistry; GeometryTests'
// byte-exact snapshot reads it (the `workspaceTypes` list). Joining one xUnit
// Collection makes them run serially so a mid-test registration can't leak into
// the snapshot (each test here restores the registry to just "stack").
[<Xunit.Collection("WorkspaceRegistry")>]
module WTF.Core.Tests.WorkspaceTypeTests

// Tests for the pluggable workspace-TYPE seam (#5): the WorkspaceRegistry, the
// built-in "stack" type dogfooding it (arrange delegates to it and stays
// byte-identical to the old behaviour), the real-focus access a plugin type gets,
// and the SetWorkspaceType / SetWorkspaceState reducer commands.

open Xunit
open WTF.Core

let private screen = Rect.create 0 0 1920 1080

let private win id app : WindowInfo = { Id = id; AppId = app; Title = app; Floating = false }

/// A world with `n` tiled windows on workspace "1", focus on the first added.
let private worldWith n =
    let mutable w = World.empty screen
    for i in 1..n do
        w <- fst (Reducer.apply (AddWindow(win i (sprintf "app%d" i))) w)
    w

// --- the built-in "stack" type is registered and dogfooded ------------------

[<Fact>]
let ``the built-in stack type is registered`` () =
    Assert.True(WorkspaceRegistry.has "stack")
    Assert.Contains("stack", WorkspaceRegistry.names ())

[<Fact>]
let ``arrange delegates to the stack type and matches calling it directly`` () =
    let w = worldWith 3
    let ws = World.currentWorkspace w
    let direct = World.stackArranger (World.viewOf ws w)
    Assert.Equal<(WindowId * Rect) list>(direct, World.arrange w)
    // and it actually placed all three tiled windows
    Assert.Equal(3, (World.arrange w).Length)

[<Fact>]
let ``an unknown workspace type falls back to stack (never drops the workspace)`` () =
    // Force a phantom type directly (bypassing SetWorkspaceType's guard) — arrange
    // must still place windows via the "stack" fallback, not return [].
    let w = World.setTypeOf "1" "does-not-exist" (worldWith 2)
    Assert.Equal(2, (World.arrange w).Length)

// --- a plugin-style type reads the REAL focus (obstacle #1 removed) ----------

[<Fact>]
let ``a workspace type sees the real focus, not a focus-less sub-stack`` () =
    // Register a type that places ONLY the focused window; prove the placed id
    // tracks the workspace's true Focus as it changes.
    WorkspaceRegistry.register "focus_only"
        (fun v -> match v.Stack with Some s -> [ s.Focus, v.Screen ] | None -> [])
    try
        let w = World.setTypeOf "1" "focus_only" (worldWith 3)
        let focused0 = (World.focusedWindow w).Value
        let placed0 = World.arrange w |> List.map fst
        Assert.Equal<WindowId list>([ focused0 ], placed0)
        // move focus; the placed window must follow the real focus.
        let w2 = fst (Reducer.apply (Focus NextWindow) w)
        let focused1 = (World.focusedWindow w2).Value
        Assert.NotEqual(focused0, focused1)
        Assert.Equal<WindowId list>([ focused1 ], World.arrange w2 |> List.map fst)
    finally
        // restore the built-in so the process-global override doesn't leak.
        WorkspaceRegistry.clear ()
        WorkspaceRegistry.register "stack" World.stackArranger

// --- SetWorkspaceType / SetWorkspaceState reducer commands ------------------

[<Fact>]
let ``SetWorkspaceType switches a registered type and resets state`` () =
    WorkspaceRegistry.register "fixture_t" (fun _ -> [])
    try
        let w0 = World.setStateOf "1" "old-state" (worldWith 1)
        let w1, _ = Reducer.apply (SetWorkspaceType "fixture_t") w0
        let ws = World.currentWorkspace w1
        Assert.Equal("fixture_t", ws.Type)
        Assert.Equal("", ws.State)   // switching type wipes the stale per-type state
    finally
        WorkspaceRegistry.clear ()
        WorkspaceRegistry.register "stack" World.stackArranger

[<Fact>]
let ``SetWorkspaceType with an unknown name is a no-op`` () =
    let w0 = worldWith 1
    let w1, effects = Reducer.apply (SetWorkspaceType "nope") w0
    Assert.Equal("stack", (World.currentWorkspace w1).Type)
    Assert.Empty(effects)

[<Fact>]
let ``SetWorkspaceState stores the per-type data and re-arranges`` () =
    let w0 = worldWith 1
    let w1, effects = Reducer.apply (SetWorkspaceState "viewport:42") w0
    Assert.Equal("viewport:42", (World.currentWorkspace w1).State)
    Assert.NotEmpty(effects)   // an Arrange effect was emitted

[<Fact>]
let ``SetWorkspaceType and SetWorkspaceState are undoable`` () =
    Assert.True(Reducer.isUndoable (SetWorkspaceType "x"))
    Assert.True(Reducer.isUndoable (SetWorkspaceState "y"))
