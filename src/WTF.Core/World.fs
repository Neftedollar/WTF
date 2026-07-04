namespace WTF.Core

/// The full, immutable model of the managed world. The agent-first design hinges
/// on this: because the entire WM state is one plain value, it serialises to JSON
/// for an LLM to read, and every change is a pure transition it can request.

type WindowId = int

/// What the agent reasons about per window — semantic identity, not pixels.
type WindowInfo =
    { Id: WindowId
      AppId: string      // Wayland app_id, e.g. "firefox", "foot"
      Title: string
      Floating: bool }

type Workspace =
    { Tag: string
      Layout: string                 // name resolved via the layout Registry
      // The workspace TYPE — the model of how this workspace organises windows over
      // time (arrange delegates to it). "stack" (default) = the built-in tag/stack
      // model; other types are plugins (IWtfWorkspacePlugin), chosen per workspace.
      Type: string
      // Optional, serializable per-TYPE state threaded by the reducer (e.g. a
      // scroll/pan viewport offset). Plain data so it stays replayable/undoable and
      // agent-inspectable — never a hidden mutable tree. "stack" ignores it ("").
      State: string
      Stack: Stack<WindowId> option   // None = empty workspace; holds ALL ids (tiled+floating+fullscreen)
      // AUTHORITATIVE floating set: keyset = the floating members, value = stored geometry.
      // WindowInfo.Floating is a strict mirror of this keyset, kept in lockstep by the mutators.
      Floating: Map<WindowId, Rect>
      Fullscreen: WindowId option }   // <=1 fullscreen id per workspace

type World =
    { Workspaces: Workspace list
      Current: string                // tag of the focused workspace
      Windows: Map<WindowId, WindowInfo>
      Screen: Rect
      Nmaster: int                   // master-count param for parametric layouts
      Ratio: float                   // master-width ratio
      Gaps: int }                    // inner gap around every tile

// =============================================================================
// Pluggable WORKSPACE TYPES (#5) — a workspace-type is an extension point exactly
// like a layout, but one level up: a layout is a stateless `Rect -> Stack ->
// placements`, whereas a workspace TYPE owns the whole per-workspace model —
// it reads the REAL focus (obstacle a bare layout can't: arrange builds a
// focus-less sub-stack), it decides which windows are placed on-screen (the host
// hides the rest, so the TYPE controls visibility by choosing placements), and it
// may read an optional serializable per-type State (the escape hatch for
// canvas/scroll/tree types without abandoning replay).
//
// The built-in "stack" type = today's tag/stack model, extracted verbatim and
// registered as the first type (core stops being special-cased — it dogfoods the
// seam). Everything else is a plugin (IWtfWorkspacePlugin), discovered by the same
// PluginLoader scan as layouts, chosen per workspace via `Workspace.Type`.
// =============================================================================

/// The flat, read-only view of ONE workspace handed to its type's arranger. Built
/// from a Workspace + the world's shared layout params. Carries the REAL stack
/// (with its true Focus), unlike the layout path which sees a focus-less sub-stack.
type WorkspaceView =
    { Screen: Rect
      Nmaster: int
      Ratio: float
      Gaps: int
      Layout: string                 // the workspace's layout name (for stack-like types)
      State: string                  // the workspace's per-type State (read-only input)
      Stack: Stack<WindowId> option
      Floating: Map<WindowId, Rect>
      Fullscreen: WindowId option }

/// A workspace type: a pure function from its view to the on-screen placements
/// (ascending z). Windows it omits are hidden by the host — so returning a subset
/// (e.g. a scroll strip around the focus) is how a type controls visibility. State
/// is a read-only input here; it is mutated only by the reducer (SetWorkspaceState),
/// keeping every transition replayable.
type WorkspaceArranger = WorkspaceView -> (WindowId * Rect) list

/// Named, pluggable workspace types — mirrors the layout `Registry`. The built-in
/// "stack" is registered by `module World` (it needs the layout machinery). Plugin
/// types register via `IWtfWorkspacePlugin`. `resolve` is TOTAL: an unknown name
/// falls back to "stack" so a bad/absent type never drops the workspace.
module WorkspaceRegistry =
    let private table = System.Collections.Generic.Dictionary<string, WorkspaceArranger>()

    /// Add or replace a workspace type. Last-registered wins (loader warns).
    let register name (arranger: WorkspaceArranger) = table[name] <- arranger

    let has name = table.ContainsKey name
    let names () = table.Keys |> List.ofSeq |> List.sort

    /// Resolve a type by name; None if unknown (callers fall back to "stack").
    let tryResolve name : WorkspaceArranger option =
        match table.TryGetValue name with true, a -> Some a | _ -> None

    /// Drop every registration (used by tests to isolate cases). Does NOT re-seed
    /// "stack" — the caller re-registers it (World.stackArranger) as needed, since
    /// this module is compiled before `module World` where the built-in lives.
    let clear () = table.Clear()

/// Named, pluggable layouts. This *is* the xMonad extensibility story: a layout
/// is a function, and the user's F# config registers new ones by name. The agent
/// discovers what's available via `names()`.
type LayoutFactory = int -> float -> Layout<WindowId>

module Registry =
    let private table = System.Collections.Generic.Dictionary<string, LayoutFactory>()

    /// Add or replace a layout. Called from the config to extend WTF.
    let register name (factory: LayoutFactory) = table[name] <- factory

    let names () = table.Keys |> List.ofSeq |> List.sort

    let resolve name nmaster ratio : Layout<WindowId> option =
        match table.TryGetValue name with
        | true, f -> Some(f nmaster ratio)
        | _ -> None

    // Built-in defaults (xMonad-parity set).
    do register "tall" (fun n r -> Layout.tall n r)             // master left, stack right
    do register "wide" (fun n r -> Layout.mirror (Layout.tall n r)) // master top (Mirror Tall)
    do register "bsp" (fun _ _ -> Layout.bsp)
    do register "grid" (fun _ _ -> Layout.grid)
    do register "full" (fun _ _ -> Layout.full)

module World =

    let empty screen =
        { Workspaces = [ for t in 1..9 -> { Tag = string t; Layout = "tall"; Type = "stack"; State = ""; Stack = None; Floating = Map.empty; Fullscreen = None } ]
          Current = "1"
          Windows = Map.empty
          Screen = screen
          Nmaster = 1
          Ratio = 0.5
          Gaps = 6 }

    let currentWorkspace w =
        w.Workspaces |> List.find (fun ws -> ws.Tag = w.Current)

    let focusedWindow w =
        (currentWorkspace w).Stack |> Option.map (fun s -> s.Focus)

    /// Map a transformation over the current workspace.
    let private updateCurrent f w =
        { w with
            Workspaces =
                w.Workspaces
                |> List.map (fun ws -> if ws.Tag = w.Current then f ws else ws) }

    let mapStack f w =
        updateCurrent (fun ws -> { ws with Stack = Option.map f ws.Stack }) w

    let stackOf tag w =
        (w.Workspaces |> List.find (fun ws -> ws.Tag = tag)).Stack

    /// Set a workspace's stack outright (the only way to go Some -> None).
    let setStackOf tag st w =
        { w with
            Workspaces =
                w.Workspaces
                |> List.map (fun ws -> if ws.Tag = tag then { ws with Stack = st } else ws) }

    /// Set a workspace's authoritative Floating map.
    let setFloatingOf tag m w =
        { w with
            Workspaces =
                w.Workspaces
                |> List.map (fun ws -> if ws.Tag = tag then { ws with Floating = m } else ws) }

    /// Set a workspace's Fullscreen id (or None).
    let setFullscreenOf tag fs w =
        { w with
            Workspaces =
                w.Workspaces
                |> List.map (fun ws -> if ws.Tag = tag then { ws with Fullscreen = fs } else ws) }

    /// Update a window's Floating *mirror* flag (no-op if the window is absent).
    /// Only the lockstep mutators call this, keeping the mirror == its workspace's
    /// Floating keyset at all times.
    let setWindowFloating id flag w =
        match Map.tryFind id w.Windows with
        | Some info -> { w with Windows = Map.add id { info with Floating = flag } w.Windows }
        | None -> w

    /// Default geometry for a newly-floated window: centered, ~60% of the screen.
    let defaultFloatRect (screen: Rect) : Rect =
        let width = screen.Width * 3 / 5
        let height = screen.Height * 3 / 5
        { X = screen.X + (screen.Width - width) / 2
          Y = screen.Y + (screen.Height - height) / 2
          Width = width
          Height = height }

    /// Clamp a floating rect to stay fully on-screen. Idempotent:
    /// clampFloat s (clampFloat s r) = clampFloat s r.
    let clampFloat (screen: Rect) (r: Rect) : Rect =
        let width = min r.Width screen.Width
        let height = min r.Height screen.Height
        { X = max screen.X (min r.X (screen.X + screen.Width - width))
          Y = max screen.Y (min r.Y (screen.Y + screen.Height - height))
          Width = width
          Height = height }

    /// Set a workspace's TYPE (the workspace model). Switching type resets its
    /// per-type State to "" — the old state is meaningless to a different type.
    let setTypeOf tag ty w =
        { w with
            Workspaces =
                w.Workspaces
                |> List.map (fun ws -> if ws.Tag = tag then { ws with Type = ty; State = "" } else ws) }

    /// Set a workspace's per-type State (the serializable escape-hatch data). The
    /// active workspace type reads this in its arranger; only this mutator writes it.
    let setStateOf tag st w =
        { w with
            Workspaces =
                w.Workspaces
                |> List.map (fun ws -> if ws.Tag = tag then { ws with State = st } else ws) }

    /// The BUILT-IN "stack" workspace type = the tag/stack model, in THREE z-layers
    /// (ascending z, later = on top so the compositor can raise in list order):
    ///   1. TILED      — the registered layout applied to the sub-stack of ids that
    ///                   are neither floating nor the fullscreen id (with gaps).
    ///   2. FLOATING   — each floating id at its clamped stored rect, above tiled,
    ///                   in stack order (so stack order = z among floats).
    ///   3. FULLSCREEN — the fullscreen id (if present + stacked) at the full Screen
    ///                   rect, on top.
    /// The three id-sets partition the stack, so every id gets exactly one rect.
    /// Extracted VERBATIM from the old `arrange` — it dogfoods the workspace-type
    /// seam (core is no longer special-cased). Ignores State (stateless).
    let stackArranger : WorkspaceArranger =
        fun v ->
            match v.Stack with
            | None -> []
            | Some st ->
                let allIds = Stack.toList st
                let isFs id = v.Fullscreen = Some id
                let tiledIds =
                    allIds
                    |> List.filter (fun id -> not (Map.containsKey id v.Floating) && not (isFs id))
                // LAYER 1 — TILED: build a sub-stack preserving order; focus = first tiled id
                // (the built-in layouts read only Stack.toList order, never Focus identity).
                let tiledRects =
                    match Stack.ofList tiledIds with
                    | None -> []
                    | Some sub ->
                        // Resolve the workspace's layout; if its name is unknown (e.g. set
                        // by a hot-reloaded config that bypassed SetLayout's guard), fall
                        // back to the first registered layout rather than silently dropping
                        // every tiled window from arrange (no-loss / bijection invariant).
                        let layoutOpt =
                            match Registry.resolve v.Layout v.Nmaster v.Ratio with
                            | Some l -> Some l
                            | None ->
                                Registry.names ()
                                |> List.tryHead
                                |> Option.bind (fun n -> Registry.resolve n v.Nmaster v.Ratio)
                        match layoutOpt with
                        | Some layout ->
                            let layout = if v.Gaps > 0 then Layout.withGaps (v.Gaps * 1<lpx>) layout else layout
                            // TOTAL: a registered layout may be a reflectively-loaded PLUGIN
                            // (arbitrary user assembly). A throw here runs inside the C->F#
                            // callbacks (map/unmap/key/resize), so swallow to []-tiling rather
                            // than unwind into native code and abort the session.
                            (try layout v.Screen sub
                             with ex -> eprintfn "WTF: layout '%s' threw (skipped): %O" v.Layout ex; [])
                        | None -> []
                // LAYER 2 — FLOATING: stack order = z; skip the fs id; clamp on-screen.
                let floatRects =
                    allIds
                    |> List.choose (fun id ->
                        if isFs id then None
                        else v.Floating |> Map.tryFind id |> Option.map (fun r -> id, clampFloat v.Screen r))
                // LAYER 3 — FULLSCREEN: the fs id (guarded: must be stacked) at full Screen.
                let fsRects =
                    match v.Fullscreen with
                    | Some id when List.contains id allIds -> [ id, v.Screen ]
                    | _ -> []
                tiledRects @ floatRects @ fsRects

    // Register the built-in "stack" type. Core dogfoods the seam: `arrange`
    // delegates to a REGISTERED type, exactly like it resolves a REGISTERED layout.
    do WorkspaceRegistry.register "stack" stackArranger

    /// Build the read-only WorkspaceView for a workspace (its type's arranger input).
    let viewOf (ws: Workspace) (w: World) : WorkspaceView =
        { Screen = w.Screen
          Nmaster = w.Nmaster
          Ratio = w.Ratio
          Gaps = w.Gaps
          Layout = ws.Layout
          State = ws.State
          Stack = ws.Stack
          Floating = ws.Floating
          Fullscreen = ws.Fullscreen }

    /// Place the current workspace's windows by delegating to its TYPE's arranger
    /// (resolved from `WorkspaceRegistry`; TOTAL — an unknown/plugin-absent type
    /// falls back to "stack" rather than dropping the workspace). Windows the type
    /// omits are hidden by the host, so the type owns visibility. A plugin arranger
    /// is arbitrary user code, so a throw is swallowed to []-placement rather than
    /// unwinding into the C->F# callback that invoked it.
    let arrange w : (WindowId * Rect) list =
        let ws = currentWorkspace w
        let arranger =
            WorkspaceRegistry.tryResolve ws.Type
            |> Option.defaultValue stackArranger
        try arranger (viewOf ws w)
        with ex -> eprintfn "WTF: workspace type '%s' threw (fell back to []): %O" ws.Type ex; []
