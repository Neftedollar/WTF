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
      Stack: Stack<WindowId> option } // None = empty workspace

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
        { Workspaces = [ for t in 1..9 -> { Tag = string t; Layout = "tall"; Stack = None } ]
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

    /// Run the current workspace's layout — the rectangles the compositor
    /// applies — with the configured inner gaps folded in.
    let arrange w : (WindowId * Rect) list =
        let ws = currentWorkspace w
        match ws.Stack, Registry.resolve ws.Layout w.Nmaster w.Ratio with
        | Some st, Some layout ->
            let layout = if w.Gaps > 0 then Layout.withGaps w.Gaps layout else layout
            layout w.Screen st
        | _ -> []
