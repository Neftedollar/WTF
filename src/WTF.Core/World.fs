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
        { Workspaces = [ for t in 1..9 -> { Tag = string t; Layout = "tall"; Stack = None; Floating = Map.empty; Fullscreen = None } ]
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

    /// Run the current workspace's layout, in THREE z-layers (ascending z, later =
    /// on top so the compositor can raise in list order):
    ///   1. TILED      — the registered layout applied to the sub-stack of ids that
    ///                   are neither floating nor the fullscreen id (with gaps).
    ///   2. FLOATING   — each floating id at its clamped stored rect, above tiled,
    ///                   in stack order (so stack order = z among floats).
    ///   3. FULLSCREEN — the fullscreen id (if present + stacked) at the full Screen
    ///                   rect, on top.
    /// The three id-sets partition the stack, so every id gets exactly one rect.
    let arrange w : (WindowId * Rect) list =
        let ws = currentWorkspace w
        match ws.Stack with
        | None -> []
        | Some st ->
            let allIds = Stack.toList st
            let isFs id = ws.Fullscreen = Some id
            let tiledIds =
                allIds
                |> List.filter (fun id -> not (Map.containsKey id ws.Floating) && not (isFs id))
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
                        match Registry.resolve ws.Layout w.Nmaster w.Ratio with
                        | Some l -> Some l
                        | None ->
                            Registry.names ()
                            |> List.tryHead
                            |> Option.bind (fun n -> Registry.resolve n w.Nmaster w.Ratio)
                    match layoutOpt with
                    | Some layout ->
                        let layout = if w.Gaps > 0 then Layout.withGaps (w.Gaps * 1<lpx>) layout else layout
                        layout w.Screen sub
                    | None -> []
            // LAYER 2 — FLOATING: stack order = z; skip the fs id; clamp on-screen.
            let floatRects =
                allIds
                |> List.choose (fun id ->
                    if isFs id then None
                    else ws.Floating |> Map.tryFind id |> Option.map (fun r -> id, clampFloat w.Screen r))
            // LAYER 3 — FULLSCREEN: the fs id (guarded: must be stacked) at full Screen.
            let fsRects =
                match ws.Fullscreen with
                | Some id when List.contains id allIds -> [ id, w.Screen ]
                | _ -> []
            tiledRects @ floatRects @ fsRects
