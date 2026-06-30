namespace WTF.Core

/// The high-level, user-facing configuration surface — xMonad's `XConfig`, but
/// expressed through F# computation expressions over our object model. The whole
/// point: a user (or an agent generating config) writes declarative blocks, not
/// imperative wiring.

/// What a ManageHook rule decides for a freshly-mapped window.
type ManageAction =
    | ShiftToWorkspace of string
    | FloatWindow
    | NoAction

type ManageRule = WindowInfo -> ManageAction

/// xMonad's XConfig analog.
type WtfConfig =
    { ModKey: string                    // "Super" | "Alt"
      Terminal: string
      Workspaces: string list
      DefaultLayout: string
      Gaps: int
      BorderWidth: int
      Keys: (string * Command) list     // chord -> intent
      ManageHook: ManageRule list
      StartupApps: string list
      InactiveOpacity: float            // appearance: unfocused window opacity
      AnimSpeed: float                  // appearance: animation easing speed
      ActiveBorder: string              // appearance: focused border color (#hex)
      InactiveBorder: string            // appearance: unfocused border color (#hex)
      CornerRadius: int                 // appearance: rounded corners (scenefx)
      Blur: bool }                      // appearance: backdrop blur (scenefx)

module WtfConfig =
    let defaults =
        { ModKey = "Super"
          Terminal = "foot"
          Workspaces = [ for i in 1..9 -> string i ]
          DefaultLayout = "tall"
          Gaps = 6
          BorderWidth = 2
          Keys = []
          ManageHook = []
          StartupApps = []
          InactiveOpacity = 0.94
          AnimSpeed = 0.30
          ActiveBorder = "#89b4fa"
          InactiveBorder = "#45475a"
          CornerRadius = 0
          Blur = false }

// --- predicate helpers for manage rules (read like English) ---
[<AutoOpen>]
module ManagePredicates =
    let appIs name : WindowInfo -> bool = fun i -> i.AppId = name
    let titleContains (sub: string) : WindowInfo -> bool =
        fun i -> i.Title.Contains(sub)
    let anyWindow: WindowInfo -> bool = fun _ -> true

// =====================  computation expressions  =====================

/// `keymap { bind "M-Return" (Spawn "foot"); ... }` -> (chord * Command) list
type KeymapBuilder() =
    member _.Yield(_) : (string * Command) list = []
    member _.Zero() : (string * Command) list = []
    member _.Run(xs) = List.rev xs
    [<CustomOperation "bind">]
    member _.Bind(xs, chord, cmd) = (chord, cmd) :: xs

/// `manage { rule (appIs "firefox") (ShiftToWorkspace "2"); ... }` -> ManageRule list
type ManageBuilder() =
    member _.Yield(_) : ManageRule list = []
    member _.Zero() : ManageRule list = []
    member _.Run(xs) = List.rev xs
    [<CustomOperation "rule">]
    member _.Rule(xs, predicate, action) =
        (fun info -> if predicate info then action else NoAction) :: xs

/// `config { modKey "Super"; terminal "foot"; keys (keymap {...}) }` -> WtfConfig
type ConfigBuilder() =
    member _.Yield(_) = WtfConfig.defaults
    member _.Zero() = WtfConfig.defaults
    member _.Run(c: WtfConfig) = c
    [<CustomOperation "modKey">]
    member _.ModKey(c, v) = { c with ModKey = v }
    [<CustomOperation "terminal">]
    member _.Terminal(c, v) = { c with Terminal = v }
    [<CustomOperation "workspaces">]
    member _.Workspaces(c: WtfConfig, v) = { c with Workspaces = v }
    [<CustomOperation "defaultLayout">]
    member _.DefaultLayout(c, v) = { c with DefaultLayout = v }
    [<CustomOperation "gaps">]
    member _.Gaps(c, v) = { c with Gaps = v }
    [<CustomOperation "borderWidth">]
    member _.BorderWidth(c, v) = { c with BorderWidth = v }
    [<CustomOperation "keys">]
    member _.Keys(c, ks) = { c with Keys = c.Keys @ ks }
    [<CustomOperation "manageHook">]
    member _.Manage(c, rs) = { c with ManageHook = c.ManageHook @ rs }
    [<CustomOperation "startup">]
    member _.Startup(c, apps) = { c with StartupApps = c.StartupApps @ apps }
    [<CustomOperation "inactiveOpacity">]
    member _.InactiveOpacity(c, v) = { c with InactiveOpacity = v }
    [<CustomOperation "animSpeed">]
    member _.AnimSpeed(c, v) = { c with AnimSpeed = v }
    [<CustomOperation "activeBorder">]
    member _.ActiveBorder(c, v) = { c with ActiveBorder = v }
    [<CustomOperation "inactiveBorder">]
    member _.InactiveBorder(c, v) = { c with InactiveBorder = v }
    [<CustomOperation "cornerRadius">]
    member _.CornerRadius(c, v) = { c with CornerRadius = v }
    [<CustomOperation "blur">]
    member _.Blur(c, v) = { c with Blur = v }

/// `agent { focusApp "firefox"; layout "bsp"; moveTo "2" }` -> Command list.
/// Agent-first: an LLM (or a script) expresses a *program of intents* declaratively,
/// then it runs through `Reducer.applyMany`.
type AgentBuilder() =
    member _.Yield(_) : Command list = []
    member _.Zero() : Command list = []
    member _.Run(xs) = List.rev xs
    [<CustomOperation "focusApp">]
    member _.FocusApp(xs, app) = Focus(ByApp app) :: xs
    [<CustomOperation "focusNext">]
    member _.FocusNext(xs) = Focus NextWindow :: xs
    [<CustomOperation "focusPrev">]
    member _.FocusPrev(xs) = Focus PrevWindow :: xs
    [<CustomOperation "spawn">]
    member _.Spawn(xs, p) = Spawn p :: xs
    [<CustomOperation "layout">]
    member _.Layout(xs, n) = SetLayout n :: xs
    [<CustomOperation "workspace">]
    member _.Workspace(xs, t) = SwitchWorkspace t :: xs
    [<CustomOperation "moveTo">]
    member _.MoveTo(xs, t) = MoveToWorkspace t :: xs
    [<CustomOperation "master">]
    member _.Master(xs, n) = SetMaster n :: xs
    [<CustomOperation "ratio">]
    member _.Ratio(xs, r) = SetRatio r :: xs
    [<CustomOperation "close">]
    member _.Close(xs) = CloseFocused :: xs

[<AutoOpen>]
module Builders =
    let keymap = KeymapBuilder()
    let manage = ManageBuilder()
    let config = ConfigBuilder()
    let agent = AgentBuilder()

// =====================  applying the config  =====================

module Keymap =
    /// Resolve a chord (e.g. "M-Return") to its bound intent, if any.
    let lookup (cfg: WtfConfig) (chord: string) : Command option =
        cfg.Keys |> List.tryPick (fun (c, cmd) -> if c = chord then Some cmd else None)

module Manage =
    /// Run the ManageHook when a window is mapped: add it, then apply the first
    /// matching rule (send to a workspace, float, …). The compositor calls this.
    let onAdd (cfg: WtfConfig) (info: WindowInfo) (w: World) : World * Effect list =
        let w1, e1 = Reducer.apply (AddWindow info) w
        let action =
            cfg.ManageHook
            |> List.map (fun rule -> rule info)
            |> List.tryFind (fun a -> a <> NoAction)
            |> Option.defaultValue NoAction
        match action with
        | ShiftToWorkspace tag ->
            let w2, e2 = Reducer.apply (MoveToWorkspace tag) w1
            w2, e1 @ e2
        | FloatWindow -> w1, e1 // floating model lands with the renderer milestone
        | NoAction -> w1, e1
