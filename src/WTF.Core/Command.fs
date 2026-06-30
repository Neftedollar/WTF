namespace WTF.Core

/// How the agent (or a keybind) points at a window without knowing pixels.
type Selector =
    | Focused
    | ById of WindowId
    | NextWindow
    | PrevWindow
    | ByApp of string          // first window whose AppId matches

/// Semantic intents — the agent-first vocabulary. Note these are *what to do*,
/// never *which key to press*: an LLM issues `Focus (ByApp "firefox")`, not a
/// synthetic Super+J. Keybinds and the agent both compile down to these.
type Command =
    | Focus of Selector
    | FocusMaster                    // focus the master (top) window
    | SwapNext                       // move focused window down the stack
    | SwapPrev
    | SwapMaster                     // promote the focused window to master
    | ToggleFloat                    // float/sink the focused window (toggle)
    | ToggleFullscreen               // fullscreen/restore the focused window (toggle)
    | SinkAll                        // clear all floating on the current workspace
    | CloseFocused
    | Spawn of string                // launch a program (effect, run by C side)
    | SwitchWorkspace of string      // view another tag
    | MoveToWorkspace of string      // send focused window to a tag
    | NextWorkspace                  // view the next/previous tag in order
    | PrevWorkspace
    | SetLayout of string            // by registry name
    | NextLayout                     // cycle the registry layouts
    | SetMaster of int
    | IncMaster                      // nmaster +/- 1
    | DecMaster
    | SetRatio of float
    | SetGaps of int                 // inner gap size
    | IncGaps
    | DecGaps
    | SetInactiveOpacity of float    // renderer knob — live appearance control
    | SetAnimationSpeed of float     // renderer knob
    | SetBorderWidth of int          // window border thickness
    | SetBorderColor of bool * float * float * float  // active?, r, g, b (0..1)
    | SetCornerRadius of int         // rounded corners (scenefx)
    | SetBlur of bool                // backdrop blur (scenefx)
    // history / session — first-class in the vocabulary, but carried out by the
    // host (history lives outside the pure World); the reducer treats them as no-ops:
    | Undo                           // revert the last undoable World change
    | Redo                           // re-apply an undone change
    | SaveSession                    // persist the canonical World to disk
    | LoadSession                    // restore a saved session
    // emitted by the compositor, not the agent:
    | AddWindow of WindowInfo        // a surface was mapped
    | RemoveWindow of WindowId       // a surface was unmapped

/// Side effects the pure reducer requests; the C compositor carries them out.
type Effect =
    | SpawnProcess of string
    | CloseSurface of WindowId
    | Arrange of (WindowId * Rect) list   // place (and later animate) windows
    | SetFullscreen of WindowId * bool    // flip a surface's fullscreen protocol flag (C)
    | RenderOpacity of float              // set inactive-window opacity (renderer)
    | RenderAnimSpeed of float            // set animation easing speed (renderer)
    | RenderBorderWidth of int            // set window border thickness
    | RenderBorderColor of bool * float * float * float  // active?, r, g, b
    | RenderCornerRadius of int           // rounded corners (scenefx)
    | RenderBlur of bool                  // backdrop blur (scenefx)

module Reducer =

    /// Re-point the focus of a stack onto a specific id (no-op if absent).
    let private focusId id st = Stack.focus id st

    /// Which commands record an undo point: user-initiated, reversible *World*
    /// mutations only. Everything else is excluded — compositor lifecycle
    /// (Add/RemoveWindow) can't be safely reverted against live surfaces;
    /// Spawn/CloseFocused are irreversible or produce no World delta; the
    /// renderer knobs change appearance, not World; and the history/session
    /// commands themselves are host-handled.
    let isUndoable (cmd: Command) : bool =
        match cmd with
        | Focus _ | FocusMaster
        | SwapNext | SwapPrev | SwapMaster
        | ToggleFloat | ToggleFullscreen | SinkAll
        | SwitchWorkspace _ | MoveToWorkspace _ | NextWorkspace | PrevWorkspace
        | SetLayout _ | NextLayout
        | SetMaster _ | IncMaster | DecMaster
        | SetRatio _
        | SetGaps _ | IncGaps | DecGaps -> true
        | CloseFocused | Spawn _
        | SetInactiveOpacity _ | SetAnimationSpeed _ | SetBorderWidth _
        | SetBorderColor _ | SetCornerRadius _ | SetBlur _
        | Undo | Redo | SaveSession | LoadSession
        | AddWindow _ | RemoveWindow _ -> false

    let private resolveSelector (w: World) sel (st: Stack<WindowId>) =
        match sel with
        | Focused -> st
        | NextWindow -> Stack.focusDown st
        | PrevWindow -> Stack.focusUp st
        | ById id -> focusId id st
        | ByApp app ->
            match
                Stack.toList st
                |> List.tryFind (fun id ->
                    match Map.tryFind id w.Windows with
                    | Some info -> info.AppId = app
                    | None -> false)
            with
            | Some id -> focusId id st
            | None -> st

    /// The heart of the agent-first design: a pure transition.
    /// `apply cmd world` -> the new world and the effects the compositor must run.
    /// Most commands end by re-issuing an `Arrange` so the C side stays in sync.
    let apply (cmd: Command) (w: World) : World * Effect list =
        let arrangeOf world = [ Arrange(World.arrange world) ]

        // step the current workspace `dir` places through the tag order (wraps)
        let cycleWorkspace dir =
            let tags = w.Workspaces |> List.map (fun ws -> ws.Tag)
            let n = List.length tags
            let i = defaultArg (List.tryFindIndex ((=) w.Current) tags) 0
            let w' = { w with Current = List.item (((i + dir) % n + n) % n) tags }
            w', arrangeOf w'

        match cmd with
        | Focus sel ->
            let w' = World.mapStack (resolveSelector w sel) w
            w', arrangeOf w'

        | FocusMaster ->
            let w' =
                World.mapStack
                    (fun s ->
                        match Stack.toList s with
                        | m :: _ -> focusId m s
                        | [] -> s)
                    w
            w', arrangeOf w'

        | SwapMaster ->
            let w' =
                World.mapStack
                    (fun s -> { Focus = s.Focus; Up = []; Down = Stack.toList s |> List.filter ((<>) s.Focus) })
                    w
            w', arrangeOf w'

        | SwapNext ->
            let w' = World.mapStack Stack.swapDown w
            w', arrangeOf w'
        | SwapPrev ->
            let w' = World.mapStack Stack.swapUp w
            w', arrangeOf w'

        | ToggleFloat ->
            // Toggle the focused window's floating state, mirroring WindowInfo.Floating
            // in lockstep. If it is also the fullscreen id it stays fullscreen (the
            // fullscreen layer wins in arrange) — documented edge, no special-casing.
            match World.focusedWindow w with
            | None -> w, []
            | Some id ->
                let ws = World.currentWorkspace w
                let isFloating = Map.containsKey id ws.Floating
                let newFloating =
                    if isFloating then Map.remove id ws.Floating
                    else Map.add id (World.clampFloat w.Screen (World.defaultFloatRect w.Screen)) ws.Floating
                let w' =
                    w
                    |> World.setFloatingOf w.Current newFloating
                    |> World.setWindowFloating id (not isFloating)
                w', arrangeOf w'

        | ToggleFullscreen ->
            // Toggle the focused window as the workspace's single fullscreen id.
            // Emit SetFullscreen only when the protocol state actually flips.
            match World.focusedWindow w with
            | None -> w, []
            | Some id ->
                let ws = World.currentWorkspace w
                match ws.Fullscreen with
                | Some cur when cur = id ->
                    let w' = World.setFullscreenOf w.Current None w
                    w', SetFullscreen(id, false) :: arrangeOf w'
                | Some old ->
                    let w' = World.setFullscreenOf w.Current (Some id) w
                    w', SetFullscreen(old, false) :: SetFullscreen(id, true) :: arrangeOf w'
                | None ->
                    let w' = World.setFullscreenOf w.Current (Some id) w
                    w', SetFullscreen(id, true) :: arrangeOf w'

        | SinkAll ->
            // Clear all floating on the current workspace, restoring mirrors.
            let ws = World.currentWorkspace w
            let ids = ws.Floating |> Map.toList |> List.map fst
            let w' =
                (w |> World.setFloatingOf w.Current Map.empty, ids)
                ||> List.fold (fun acc id -> World.setWindowFloating id false acc)
            w', arrangeOf w'

        | CloseFocused ->
            match World.focusedWindow w with
            | Some id -> w, [ CloseSurface id ]   // C unmaps; RemoveWindow follows
            | None -> w, []

        | Spawn prog -> w, [ SpawnProcess prog ]

        | SwitchWorkspace tag ->
            if List.exists (fun ws -> ws.Tag = tag) w.Workspaces then
                let w' = { w with Current = tag }
                w', arrangeOf w'
            else w, []

        | MoveToWorkspace tag ->
            match World.focusedWindow w with
            | Some id when tag <> w.Current && List.exists (fun ws -> ws.Tag = tag) w.Workspaces ->
                // the focused window IS `id`, so deleting the focus removes it
                let newCur = World.stackOf w.Current w |> Option.bind Stack.delete
                let tgt =
                    match World.stackOf tag w with
                    | Some s -> Stack.insertUp id s
                    | None -> Stack.singleton id
                // Sink the moved window: drop it from the source workspace's floating
                // set + clear its fullscreen if it was the one, and reset the mirror, so
                // it lands plainly tiled on the destination.
                let src = World.currentWorkspace w
                let srcFloating = Map.remove id src.Floating
                let srcFullscreen = if src.Fullscreen = Some id then None else src.Fullscreen
                let w' =
                    w
                    |> World.setStackOf w.Current newCur
                    |> World.setStackOf tag (Some tgt)
                    |> World.setFloatingOf w.Current srcFloating
                    |> World.setFullscreenOf w.Current srcFullscreen
                    |> World.setWindowFloating id false
                // If the moved window was fullscreen, clear the surface flag too —
                // otherwise the client keeps rendering fullscreen on its new (tiled)
                // home until something else toggles it.
                let clearFs = if src.Fullscreen = Some id then [ SetFullscreen(id, false) ] else []
                w', clearFs @ arrangeOf w'
            | _ -> w, []

        | SetLayout name ->
            if Registry.resolve name w.Nmaster w.Ratio |> Option.isSome then
                let w' =
                    { w with
                        Workspaces =
                            w.Workspaces
                            |> List.map (fun ws ->
                                if ws.Tag = w.Current then { ws with Layout = name } else ws) }
                w', arrangeOf w'
            else w, []

        | NextWorkspace -> cycleWorkspace 1
        | PrevWorkspace -> cycleWorkspace -1

        | NextLayout ->
            let names = Registry.names ()
            if List.isEmpty names then
                w, []
            else
                let cur = (World.currentWorkspace w).Layout
                let i = defaultArg (List.tryFindIndex ((=) cur) names) -1
                let next = List.item ((i + 1) % names.Length) names
                let w' =
                    { w with
                        Workspaces =
                            w.Workspaces
                            |> List.map (fun ws -> if ws.Tag = w.Current then { ws with Layout = next } else ws) }
                w', arrangeOf w'

        | SetMaster n ->
            let w' = { w with Nmaster = max 1 n }
            w', arrangeOf w'
        | IncMaster ->
            let w' = { w with Nmaster = w.Nmaster + 1 }
            w', arrangeOf w'
        | DecMaster ->
            let w' = { w with Nmaster = max 1 (w.Nmaster - 1) }
            w', arrangeOf w'
        | SetRatio r ->
            let w' = { w with Ratio = min 0.9 (max 0.1 r) }
            w', arrangeOf w'

        | SetGaps g ->
            let w' = { w with Gaps = max 0 g }
            w', arrangeOf w'
        | IncGaps ->
            let w' = { w with Gaps = w.Gaps + 4 }
            w', arrangeOf w'
        | DecGaps ->
            let w' = { w with Gaps = max 0 (w.Gaps - 4) }
            w', arrangeOf w'

        | SetInactiveOpacity o -> w, [ RenderOpacity(min 1.0 (max 0.0 o)) ]
        | SetAnimationSpeed s -> w, [ RenderAnimSpeed(min 1.0 (max 0.01 s)) ]
        | SetBorderWidth bw -> w, [ RenderBorderWidth(max 0 bw) ]
        | SetBorderColor(a, r, g, b) -> w, [ RenderBorderColor(a, r, g, b) ]
        | SetCornerRadius radius -> w, [ RenderCornerRadius(max 0 radius) ]
        | SetBlur on -> w, [ RenderBlur on ]

        // Host-handled (history/session). Total no-op arm keeps the reducer
        // pure and exhaustive; the real work happens in the host's dispatch.
        | Undo | Redo | SaveSession | LoadSession -> w, []

        | AddWindow info ->
            // Guard the stack-uniqueness invariant: a re-mapped id must not be
            // inserted a second time. If it is already stacked on ANY workspace,
            // just refresh its WindowInfo and leave the stacks untouched.
            let alreadyStacked =
                w.Workspaces
                |> List.exists (fun ws ->
                    match ws.Stack with
                    | Some s -> List.contains info.Id (Stack.toList s)
                    | None -> false)
            if alreadyStacked then
                let w' = { w with Windows = Map.add info.Id info w.Windows }
                w', arrangeOf w'
            else
                let newCur =
                    match World.stackOf w.Current w with
                    | Some s -> Stack.insertUp info.Id s
                    | None -> Stack.singleton info.Id
                let w' =
                    { w with Windows = Map.add info.Id info w.Windows }
                    |> World.setStackOf w.Current (Some newCur)
                w', arrangeOf w'

        | RemoveWindow id ->
            // drop the window from whichever workspace holds it; an emptied
            // workspace becomes None (Stack.delete returns None on the last item).
            // ALSO purge any dangling floating/fullscreen reference to the dead id,
            // so arrange never references a non-stacked window.
            let workspaces =
                w.Workspaces
                |> List.map (fun ws ->
                    let stack =
                        match ws.Stack with
                        | Some s when List.contains id (Stack.toList s) ->
                            // Preserve the user's current focus when a *non-focused*
                            // window dies: focus the dying id, delete it, then restore
                            // the original focus. If the focused window itself is the
                            // one removed, fall back to delete's down-then-up neighbour.
                            let keep = s.Focus
                            match focusId id s |> Stack.delete with
                            | Some s' -> Some(if keep = id then s' else focusId keep s')
                            | None -> None
                        | other -> other
                    { ws with
                        Stack = stack
                        Floating = Map.remove id ws.Floating
                        Fullscreen = (if ws.Fullscreen = Some id then None else ws.Fullscreen) })
            let w' =
                { w with Windows = Map.remove id w.Windows; Workspaces = workspaces }
            w', arrangeOf w'

    /// Fold a batch of commands, accumulating effects (handy for an agent that
    /// sends a small program of intents in one shot).
    let applyMany cmds w =
        cmds
        |> List.fold
            (fun (world, effs) c ->
                let world', e = apply c world
                world', effs @ e)
            (w, [])
