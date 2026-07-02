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

// --- input device configuration (pure data; applied by the C shim) ---

/// Per-keyboard xkb + repeat settings. Empty-string fields fall back to the xkb
/// default (the C side passes NULL to xkb_rule_names for those).
type KeyboardConfig =
    { Layout: string        // "us" or "us,ru"  ("" => xkb default => NULL)
      Variant: string       // "" => default
      Options: string       // "grp:alt_shift_toggle,ctrl:nocaps"; "" => default
      Model: string         // "" => default
      Rules: string         // "" => default
      RepeatRate: int       // keys/sec (was hardcoded 25)
      RepeatDelay: int }    // ms before repeat (was hardcoded 600)

/// Per-mouse libinput pointer settings.
type MouseConfig =
    { AccelSpeed: float      // -1.0..1.0 ; 0.0 = libinput neutral default
      AccelProfile: string   // "flat" | "adaptive" | "" => leave default
      NaturalScroll: bool }

/// Per-touchpad libinput settings.
type TouchpadConfig =
    { Tap: bool
      TapDrag: bool
      NaturalScroll: bool
      DisableWhileTyping: bool
      ScrollMethod: string   // "two-finger" | "edge" | "none" | "" => leave
      ClickMethod: string    // "button-areas" | "clickfinger" | "" => leave
      AccelSpeed: float      // -1.0..1.0
      AccelProfile: string } // "flat" | "adaptive" | "" => leave default

/// The whole input surface: applied per device TYPE as it attaches.
type InputConfig =
    { Keyboard: KeyboardConfig
      Mouse: MouseConfig
      Touchpad: TouchpadConfig }
      // DeviceOverrides deferred — per-name match would be
      // (NameSubstring:string * InputConfig) list applied after the type config.

// --- wallpaper (pure data; decode happens in the host, NOT here) ---

/// How an image wallpaper maps onto the output rectangle.
///   Fill    = cover the whole output, cropping overflow (preserve aspect)
///   Fit     = contain inside the output, letterboxing the remainder
///   Stretch = exact output size, ignoring aspect
///   Center  = original size centered (no upscale), padding the remainder
///   Tile    = repeat the image to fill the output
type WallpaperMode =
    | Fill
    | Fit
    | Stretch
    | Center
    | Tile

/// The wallpaper choice. Named `NoWallpaper` (not `None`) so it never clashes
/// with `Option.None`. `Color` is a "#rrggbb" hex string, parsed in the host via
/// `Protocol.hexColor`. `Image` carries a path (host may expand a leading `~`)
/// plus the scaling mode. WTF.Core stays ImageSharp-free: this is just data.
type Wallpaper =
    | NoWallpaper
    | Color of string
    | Image of path: string * mode: WallpaperMode
    /// A macOS-style dynamic wallpaper: a multi-frame .heic whose frames span the
    /// day (Apple's format, decoded in the host via libheif). The host shows the
    /// frame matching the time of day and switches on frame boundaries.
    | Dynamic of path: string * mode: WallpaperMode

// --- bar & omnibox (client UI) configuration -------------------------------
// PURE DATA, same philosophy as the rest of the config: the WM serializes
// these into the agent-socket snapshot (under "ui"), and the bar/omnibox —
// separate Wayland-client processes — read them from there. The bar polls the
// snapshot, so a config.fsx hot-reload restyles it live; the omnibox reads
// its config at launch (it is short-lived by design).

/// One bar segment. Order inside Left/Right lists = display order.
type BarSegment =
    | Workspaces               // workspace pills (current/occupied aware)
    | Clock of format: string  // .NET time format, e.g. "HH:mm" or "ddd HH:mm"
    | Battery
    | Network
    | Player                   // MPRIS now-playing
    | Label of string          // static text

type BarPosition =
    | Top
    | Bottom
    | Left     // vertical bar; Height acts as THICKNESS, segments stack top->bottom
    | Right

/// Colors are "#rrggbb" or "#rrggbbaa" hex strings (same convention as borders).
/// Multiple bars are supported: give each a Name and launch one wtf-bar process
/// per entry (`wtf-bar` takes the first/only bar; `wtf-bar --name status2`
/// takes the entry named "status2").
type BarConfig =
    { Name: string
      Position: BarPosition
      Height: int              // thickness: bar height (Top/Bottom) or width (Left/Right)
      FontSize: float
      Background: string       // supports alpha (#rrggbbaa) for translucency
      Foreground: string
      Dim: string              // idle workspace / secondary text
      Accent: string           // current-workspace pill / highlights
      Left: BarSegment list
      Right: BarSegment list }

module BarConfig =
    /// The built-in look (what shipped before bar config existed).
    let defaults =
        { Name = "main"
          Position = Top
          Height = 28
          FontSize = 14.0
          Background = "#1e1e2eeb"
          Foreground = "#cdd6f4"
          Dim = "#6c7086"
          Accent = "#89b4fa"
          Left = [ Workspaces ]
          Right = [ Player; Network; Battery; Clock "HH:mm" ] }

type OmniboxConfig =
    { Width: int
      Height: int
      RowHeight: int
      FontSize: float
      Background: string
      InputBackground: string
      Foreground: string
      Dim: string
      Selection: string        // selected-row background
      Prompt: string           // prompt glyph/text, e.g. ">" or "λ"
      PromptColor: string
      Placeholder: string }    // hint shown while the query is empty

module OmniboxConfig =
    let defaults =
        { Width = 640
          Height = 400
          RowHeight = 30
          FontSize = 16.0
          Background = "#181825f4"
          InputBackground = "#313244"
          Foreground = "#cdd6f4"
          Dim = "#7f849c"
          Selection = "#89b4fa"
          Prompt = ">"
          PromptColor = "#a6e3a1"
          Placeholder = "type to search apps…" }

// --- dynamic appearance model (E1: appearance as functions of context) ---

/// The per-window context an appearance knob is evaluated against. A static
/// value is just a function that ignores this. A future animation workflow ADDS
/// a `Time`/`Phase` field here (additive — every caller defaults it), so leave
/// room; do not add it now.
type RenderContext =
    { Window: WindowInfo
      Workspace: string
      Focused: bool
      /// The active wallpaper-derived palette (pure data). Defaults to
      /// `Palette.defaultPalette` at every construction site (F# records have no
      /// per-field default), so legacy/E1 behavior is byte-identical when no knob
      /// reads it. The host computes the real value from the image and passes it in.
      Palette: Palette.Palette }

/// A context-dependent value: the heart of the effects engine. `Dyn<string>` is a
/// border color that can vary per app/state; `Dyn<float>` a per-window opacity.
type Dyn<'a> = RenderContext -> 'a

module Dyn =
    let constant (x: 'a) : Dyn<'a> = fun _ -> x
    let map (f: 'a -> 'b) (d: Dyn<'a>) : Dyn<'b> = d >> f

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
      Blur: bool                        // appearance: backdrop blur (scenefx)
      Shadow: bool                      // appearance: macOS-style drop shadow (scenefx)
      ShadowSigma: float                // shadow blur spread in px
      ShadowColor: string               // shadow color (#hex)
      ShadowOpacity: float              // shadow alpha 0..1
      ShadowOffset: int * int           // shadow (dx, dy) offset in px; macOS look = (0, 8)
      Scale: float                      // HiDPI output scale (physical px per logical px); 1.0 = logical px (default)
      HistoryLimit: int                 // undo depth: max retained past states
      Input: InputConfig                // keyboard/mouse/touchpad device settings
      Wallpaper: Wallpaper              // background: solid color or decoded image
      Bars: BarConfig list              // status bar(s) styling (served to wtf-bar via the socket)
      Omnibox: OmniboxConfig            // launcher styling (served to wtf-omnibox via the socket)
      // --- E1 dynamic appearance overrides (None => fall through to the static
      // ActiveBorder/InactiveBorder/InactiveOpacity fields, i.e. today's behavior) ---
      BorderColorOf: Dyn<string> option // per-window border color (returns a #hex)
      OpacityOf: Dyn<float> option }    // per-window opacity (0..1, clamped on resolve)

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
          Blur = false
          Shadow = false
          ShadowSigma = 24.0
          ShadowColor = "#000000"
          ShadowOpacity = 0.45
          ShadowOffset = (0, 8)
          Scale = 1.0
          HistoryLimit = 64
          Input =
            { Keyboard =
                { Layout = "us"; Variant = ""; Options = ""; Model = ""; Rules = ""
                  RepeatRate = 25; RepeatDelay = 600 }
              Mouse =
                { AccelSpeed = 0.0; AccelProfile = ""; NaturalScroll = false }
              Touchpad =
                { Tap = true; TapDrag = true; NaturalScroll = true
                  DisableWhileTyping = true; ScrollMethod = "two-finger"
                  ClickMethod = "button-areas"; AccelSpeed = 0.0; AccelProfile = "" } }
          Wallpaper = Color "#1e1e2e"
          Bars = [ BarConfig.defaults ]
          Omnibox = OmniboxConfig.defaults
          BorderColorOf = None
          OpacityOf = None }

// --- E1 appearance resolution (pure + total; the host calls this per window) ---

/// The resolved, renderer-ready appearance for one window in one context.
type WindowStyle =
    { BorderColor: float * float * float   // RGB 0..1
      Opacity: float }                     // 0..1, already clamped

module Appearance =
    /// The border color #hex for a window: the configured function if any, else the
    /// existing focused?active:inactive behavior (reading the live cfg fields).
    let resolveBorderHex (cfg: WtfConfig) (ctx: RenderContext) : string =
        let fallback = if ctx.Focused then cfg.ActiveBorder else cfg.InactiveBorder
        match cfg.BorderColorOf with
        // TOTAL: a throwing user function falls back to the focused/inactive color
        // rather than unwinding (this runs inside the per-window restyle path).
        | Some f -> (try f ctx with _ -> fallback)
        | None   -> fallback

    /// The opacity for a window: the configured function if any, else focused?1.0:
    /// InactiveOpacity. Clamped to [0,1] — matches the reducer's SetInactiveOpacity.
    let resolveOpacity (cfg: WtfConfig) (ctx: RenderContext) : float =
        let fallback = if ctx.Focused then 1.0 else cfg.InactiveOpacity
        let o =
            match cfg.OpacityOf with
            // TOTAL: a throwing user function falls back rather than unwinding.
            | Some f -> (try f ctx with _ -> fallback)
            | None   -> fallback
        // Total clamp: NaN (a pathological user function) maps to fully opaque
        // rather than escaping the [0,1] range. -inf -> 0, +inf -> 1 fall out of
        // min/max naturally. For real values this is the reducer's SetInactiveOpacity clamp.
        if System.Double.IsNaN o then 1.0 else min 1.0 (max 0.0 o)

    /// Evaluate both knobs into a renderer-ready style. TOTAL: a bad hex never
    /// throws — it falls back to the InactiveBorder color, then a Catppuccin grey.
    let resolveWindowStyle (cfg: WtfConfig) (ctx: RenderContext) : WindowStyle =
        let hex = resolveBorderHex cfg ctx
        let rgb =
            Protocol.hexColor hex
            |> Option.orElse (Protocol.hexColor cfg.InactiveBorder)
            |> Option.defaultValue (0.27, 0.28, 0.35)
        { BorderColor = rgb; Opacity = resolveOpacity cfg ctx }

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
    /// Function-form border color: `borderColor (fun w -> if w.Window.AppId="firefox" then "#ff8800" else "#45475a")`.
    /// Overrides the static active/inactive logic; returns a #hex parsed via Protocol.hexColor.
    [<CustomOperation "borderColor">]
    member _.BorderColor(c, f: RenderContext -> string) = { c with BorderColorOf = Some f }
    /// Function-form per-window opacity: `windowOpacity (fun w -> if w.Window.AppId="foot" then 0.85 else 1.0)`.
    /// Result is clamped to [0,1] on resolve.
    [<CustomOperation "windowOpacity">]
    member _.WindowOpacity(c, f: RenderContext -> float) = { c with OpacityOf = Some f }
    [<CustomOperation "cornerRadius">]
    member _.CornerRadius(c, v) = { c with CornerRadius = v }
    [<CustomOperation "blur">]
    member _.Blur(c, v) = { c with Blur = v }
    [<CustomOperation "shadow">]
    member _.Shadow(c, v) = { c with Shadow = v }
    [<CustomOperation "shadowSigma">]
    member _.ShadowSigma(c, v) = { c with ShadowSigma = v }
    [<CustomOperation "shadowColor">]
    member _.ShadowColor(c, v: string) = { c with ShadowColor = v }
    [<CustomOperation "shadowOpacity">]
    member _.ShadowOpacity(c, v) = { c with ShadowOpacity = v }
    [<CustomOperation "shadowOffset">]
    member _.ShadowOffset(c, dx: int, dy: int) = { c with ShadowOffset = (dx, dy) }
    [<CustomOperation "scale">]
    member _.Scale(c, v) = { c with Scale = v }
    [<CustomOperation "historyLimit">]
    member _.HistoryLimit(c, v) = { c with HistoryLimit = v }
    [<CustomOperation "input">]
    member _.Input(c, i: InputConfig) = { c with Input = i }
    [<CustomOperation "wallpaper">]
    member _.Wallpaper(c, v: Wallpaper) = { c with Wallpaper = v }
    [<CustomOperation "bar">]
    member _.Bar(c, v: BarConfig) = { c with Bars = [ v ] }
    [<CustomOperation "bars">]
    member _.Bars(c, v: BarConfig list) = { c with Bars = v }
    [<CustomOperation "omnibox">]
    member _.Omnibox(c, v: OmniboxConfig) = { c with Omnibox = v }

// --- input sub-builders ---
// Member params are type-annotated because some field names (e.g. Layout) collide
// with other records (Workspace.Layout), so `{ c with Layout = v }` would otherwise
// be ambiguous. Each sub-builder yields its own record; `inputDevices { ... }`
// composes the three via Yield-overloads + Combine (NOT custom operations) so the
// keyboard/mouse/touchpad builder names stay usable, unshadowed, inside it.

/// `keyboard { layout "us,ru"; options "grp:alt_shift_toggle" }` -> KeyboardConfig
type KeyboardBuilder() =
    member _.Yield(_) = WtfConfig.defaults.Input.Keyboard
    member _.Zero() = WtfConfig.defaults.Input.Keyboard
    member _.Run(c: KeyboardConfig) = c
    [<CustomOperation "layout">]      member _.Layout(c: KeyboardConfig, v)      = { c with Layout = v }
    [<CustomOperation "variant">]     member _.Variant(c: KeyboardConfig, v)     = { c with Variant = v }
    [<CustomOperation "options">]     member _.Options(c: KeyboardConfig, v)     = { c with Options = v }
    [<CustomOperation "model">]       member _.Model(c: KeyboardConfig, v)       = { c with Model = v }
    [<CustomOperation "rules">]       member _.Rules(c: KeyboardConfig, v)       = { c with Rules = v }
    [<CustomOperation "repeatRate">]  member _.RepeatRate(c: KeyboardConfig, v)  = { c with RepeatRate = v }
    [<CustomOperation "repeatDelay">] member _.RepeatDelay(c: KeyboardConfig, v) = { c with RepeatDelay = v }

/// `mouse { accelProfile "flat"; accelSpeed 0.2 }` -> MouseConfig
type MouseBuilder() =
    member _.Yield(_) = WtfConfig.defaults.Input.Mouse
    member _.Zero() = WtfConfig.defaults.Input.Mouse
    member _.Run(c: MouseConfig) = c
    [<CustomOperation "accelSpeed">]    member _.AccelSpeed(c: MouseConfig, v)    = { c with AccelSpeed = v }
    [<CustomOperation "accelProfile">]  member _.AccelProfile(c: MouseConfig, v)  = { c with AccelProfile = v }
    [<CustomOperation "naturalScroll">] member _.NaturalScroll(c: MouseConfig, v) = { c with NaturalScroll = v }

/// `touchpad { tap true; naturalScroll true; disableWhileTyping true }` -> TouchpadConfig
type TouchpadBuilder() =
    member _.Yield(_) = WtfConfig.defaults.Input.Touchpad
    member _.Zero() = WtfConfig.defaults.Input.Touchpad
    member _.Run(c: TouchpadConfig) = c
    [<CustomOperation "tap">]                member _.Tap(c: TouchpadConfig, v)       = { c with Tap = v }
    [<CustomOperation "tapDrag">]           member _.TapDrag(c: TouchpadConfig, v)   = { c with TapDrag = v }
    [<CustomOperation "naturalScroll">]     member _.NatScroll(c: TouchpadConfig, v) = { c with NaturalScroll = v }
    [<CustomOperation "disableWhileTyping">]member _.Dwt(c: TouchpadConfig, v)       = { c with DisableWhileTyping = v }
    [<CustomOperation "scrollMethod">]      member _.Scroll(c: TouchpadConfig, v)    = { c with ScrollMethod = v }
    [<CustomOperation "clickMethod">]       member _.Click(c: TouchpadConfig, v)     = { c with ClickMethod = v }
    [<CustomOperation "accelSpeed">]        member _.Accel(c: TouchpadConfig, v)     = { c with AccelSpeed = v }
    [<CustomOperation "accelProfile">]      member _.Profile(c: TouchpadConfig, v)   = { c with AccelProfile = v }

/// `inputDevices { keyboard {...}; mouse {...}; touchpad {...} }` -> InputConfig.
/// Each sub-block yields an `InputConfig -> InputConfig` updater; Combine composes
/// them left-to-right and Run applies the chain to the defaults. A sub-block may be
/// omitted (keeps its default) and order does not matter.
type InputBuilder() =
    member _.Yield(_: unit) : InputConfig -> InputConfig = id
    member _.Yield(k: KeyboardConfig) : InputConfig -> InputConfig = fun i -> { i with Keyboard = k }
    member _.Yield(m: MouseConfig) : InputConfig -> InputConfig = fun i -> { i with Mouse = m }
    member _.Yield(t: TouchpadConfig) : InputConfig -> InputConfig = fun i -> { i with Touchpad = t }
    member _.Zero() : InputConfig -> InputConfig = id
    member _.Combine(f: InputConfig -> InputConfig, g: InputConfig -> InputConfig) = f >> g
    member _.Delay(f: unit -> InputConfig -> InputConfig) = f ()
    member _.Run(f: InputConfig -> InputConfig) = f WtfConfig.defaults.Input

/// `barConfig { position Bottom; accent "#f38ba8"; right [ Clock "ddd HH:mm" ] }`
/// -> BarConfig. Omitted knobs keep BarConfig.defaults. NB: geometry knobs
/// (position/height) apply when the bar STARTS; colors/segments/font restyle
/// a running bar live on the next snapshot poll (~1s).
type BarConfigBuilder() =
    member _.Yield(_) = BarConfig.defaults
    [<CustomOperation "name">]
    member _.Name(c: BarConfig, v: string) = { c with Name = v }
    [<CustomOperation "position">]
    member _.Position(c: BarConfig, v) = { c with Position = v }
    [<CustomOperation "height">]
    member _.Height(c: BarConfig, v) = { c with Height = v }
    [<CustomOperation "fontSize">]
    member _.FontSize(c: BarConfig, v) = { c with FontSize = v }
    [<CustomOperation "background">]
    member _.Background(c: BarConfig, v: string) = { c with Background = v }
    [<CustomOperation "foreground">]
    member _.Foreground(c: BarConfig, v: string) = { c with Foreground = v }
    [<CustomOperation "dim">]
    member _.Dim(c: BarConfig, v: string) = { c with Dim = v }
    [<CustomOperation "accent">]
    member _.Accent(c: BarConfig, v: string) = { c with Accent = v }
    [<CustomOperation "left">]
    member _.Left(c: BarConfig, v: BarSegment list) = { c with Left = v }
    [<CustomOperation "right">]
    member _.Right(c: BarConfig, v: BarSegment list) = { c with Right = v }

/// `omniboxConfig { width 720; selection "#f38ba8"; prompt "λ" }` -> OmniboxConfig.
/// Applied when the omnibox LAUNCHES (it is a short-lived process by design).
type OmniboxConfigBuilder() =
    member _.Yield(_) = OmniboxConfig.defaults
    [<CustomOperation "width">]
    member _.Width(c: OmniboxConfig, v) = { c with Width = v }
    [<CustomOperation "height">]
    member _.Height(c: OmniboxConfig, v) = { c with Height = v }
    [<CustomOperation "rowHeight">]
    member _.RowHeight(c: OmniboxConfig, v) = { c with RowHeight = v }
    [<CustomOperation "fontSize">]
    member _.FontSize(c: OmniboxConfig, v) = { c with FontSize = v }
    [<CustomOperation "background">]
    member _.Background(c: OmniboxConfig, v: string) = { c with Background = v }
    [<CustomOperation "inputBackground">]
    member _.InputBackground(c: OmniboxConfig, v: string) = { c with InputBackground = v }
    [<CustomOperation "foreground">]
    member _.Foreground(c: OmniboxConfig, v: string) = { c with Foreground = v }
    [<CustomOperation "dim">]
    member _.Dim(c: OmniboxConfig, v: string) = { c with Dim = v }
    [<CustomOperation "selection">]
    member _.Selection(c: OmniboxConfig, v: string) = { c with Selection = v }
    [<CustomOperation "prompt">]
    member _.Prompt(c: OmniboxConfig, v: string) = { c with Prompt = v }
    [<CustomOperation "promptColor">]
    member _.PromptColor(c: OmniboxConfig, v: string) = { c with PromptColor = v }
    [<CustomOperation "placeholder">]
    member _.Placeholder(c: OmniboxConfig, v: string) = { c with Placeholder = v }

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
    let keyboard = KeyboardBuilder()
    let mouse = MouseBuilder()
    let touchpad = TouchpadBuilder()
    // Named `inputDevices` (not `input`) because the ConfigBuilder `input` custom
    // operation would otherwise shadow it inside a `config { ... }` block.
    let inputDevices = InputBuilder()
    // Same reasoning: the ConfigBuilder ops are `bar`/`omnibox`, so the builders
    // get the -Config suffix: `bar (barConfig { ... })`.
    let barConfig = BarConfigBuilder()
    let omniboxConfig = OmniboxConfigBuilder()

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
            // TOTAL: a user rule predicate/action may throw; degrade that rule to
            // NoAction (window still maps) instead of unwinding into the C map
            // callback and aborting the session.
            |> List.map (fun rule -> try rule info with _ -> NoAction)
            |> List.tryFind (fun a -> a <> NoAction)
            |> Option.defaultValue NoAction
        match action with
        | ShiftToWorkspace tag ->
            let w2, e2 = Reducer.apply (MoveToWorkspace tag) w1
            w2, e1 @ e2
        | FloatWindow ->
            // The just-added window is the focus (AddWindow uses insertUp), so
            // ToggleFloat floats it with a default rect + mirror in lockstep.
            let w2, e2 = Reducer.apply ToggleFloat w1
            w2, e1 @ e2
        | NoAction -> w1, e1

// =====================  client UI serialization  =====================
// The WM serves BarConfig/OmniboxConfig to the bar/omnibox processes by
// splicing this JSON into the agent-socket snapshot under "ui". The clients
// parse it DEFENSIVELY (they deliberately do not reference WTF.Core), so this
// shape is a wire contract: keep it stable, additive-only.
module ClientUi =
    open System.Text.Json.Nodes

    let private segmentJson (s: BarSegment) : JsonNode =
        match s with
        | Workspaces -> JsonValue.Create "workspaces" :> JsonNode
        | Battery -> JsonValue.Create "battery" :> JsonNode
        | Network -> JsonValue.Create "network" :> JsonNode
        | Player -> JsonValue.Create "player" :> JsonNode
        | Clock fmt ->
            let o = JsonObject()
            o["clock"] <- JsonValue.Create fmt
            o :> JsonNode
        | Label text ->
            let o = JsonObject()
            o["label"] <- JsonValue.Create text
            o :> JsonNode

    let private barJson (bar: BarConfig) : JsonNode =
        let b = JsonObject()
        b["name"] <- JsonValue.Create bar.Name
        b["position"] <- JsonValue.Create(
            match bar.Position with
            | Top -> "top" | Bottom -> "bottom" | Left -> "left" | Right -> "right")
        b["height"] <- JsonValue.Create bar.Height
        b["fontSize"] <- JsonValue.Create bar.FontSize
        b["background"] <- JsonValue.Create bar.Background
        b["foreground"] <- JsonValue.Create bar.Foreground
        b["dim"] <- JsonValue.Create bar.Dim
        b["accent"] <- JsonValue.Create bar.Accent
        b["left"] <- JsonArray(bar.Left |> List.map segmentJson |> Array.ofList)
        b["right"] <- JsonArray(bar.Right |> List.map segmentJson |> Array.ofList)
        b :> JsonNode

    /// The "ui" object for the snapshot: { bars = [...]; omnibox = {...} }.
    let json (bars: BarConfig list) (omnibox: OmniboxConfig) : JsonNode =
        let o = JsonObject()
        o["width"] <- JsonValue.Create omnibox.Width
        o["height"] <- JsonValue.Create omnibox.Height
        o["rowHeight"] <- JsonValue.Create omnibox.RowHeight
        o["fontSize"] <- JsonValue.Create omnibox.FontSize
        o["background"] <- JsonValue.Create omnibox.Background
        o["inputBackground"] <- JsonValue.Create omnibox.InputBackground
        o["foreground"] <- JsonValue.Create omnibox.Foreground
        o["dim"] <- JsonValue.Create omnibox.Dim
        o["selection"] <- JsonValue.Create omnibox.Selection
        o["prompt"] <- JsonValue.Create omnibox.Prompt
        o["promptColor"] <- JsonValue.Create omnibox.PromptColor
        o["placeholder"] <- JsonValue.Create omnibox.Placeholder
        let ui = JsonObject()
        ui["bars"] <- JsonArray(bars |> List.map barJson |> Array.ofList)
        ui["omnibox"] <- o
        ui :> JsonNode
