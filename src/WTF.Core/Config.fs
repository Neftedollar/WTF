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

/// A shell-command bar widget: the host polls `Exec` every `IntervalMs` ms on a
/// background thread and shows its first stdout line (waybar/polybar-style). The
/// command runs via `/bin/sh -c`; a nonzero exit / missing binary shows empty.
type ScriptWidget =
    { Exec: string
      IntervalMs: int }

/// The read-model a `Custom` bar widget receives — a FLAT snapshot of the live
/// WM + desktop state in primitives, filled by the host each snapshot. Flat (not
/// the internal World/DesktopState) so config.fsx, which sees only WTF.Core,
/// needs no WTF.Desktop reference and gets a stable, documented surface.
type BarContext =
    { Windows: WindowInfo list                    // every managed window
      FocusedTitle: string                        // "" when nothing is focused
      FocusedApp: string
      Workspace: string                           // current workspace tag
      OccupiedTags: string list                   // tags with >= 1 window
      Battery: (float * string) option            // percent 0..100, state label
      Network: string option                      // connectivity state label
      Player: (string * string * string) option   // status, title, artist (first player)
      Time: System.DateTime }

module BarContext =
    /// Empty context — the safe default for tests and for a snapshot taken before
    /// any window/desktop state exists.
    let empty : BarContext =
        { Windows = []
          FocusedTitle = ""
          FocusedApp = ""
          Workspace = ""
          OccupiedTags = []
          Battery = None
          Network = None
          Player = None
          Time = System.DateTime.MinValue }

/// One bar segment. Order inside Left/Right lists = display order.
type BarSegment =
    | Workspaces               // workspace pills (current/occupied aware)
    | Clock of format: string  // .NET time format, e.g. "HH:mm" or "ddd HH:mm"
    | Battery
    | Network
    | Player                   // MPRIS now-playing
    | Label of string          // static text
    | Custom of (BarContext -> string)   // user fn, resolved host-side each snapshot
    | Script of ScriptWidget             // shell command, polled host-side

module BarSegment =
    /// Set the first time a Custom widget throws, so the diagnostic is emitted
    /// once per process, not once per snapshot.
    let mutable private warnedThrow = false

    /// Resolve a `Custom` widget to display text. TOTAL: a throwing widget shows
    /// empty (never breaks the snapshot) and is logged once.
    let resolveCustom (ctx: BarContext) (f: BarContext -> string) : string =
        try
            f ctx
        with ex ->
            if not warnedThrow then
                warnedThrow <- true
                eprintfn "WTF.Config: a Custom bar widget threw (%s); it shows empty. Logged once." ex.Message
            ""

type BarPosition =
    | Top
    | Bottom
    | Left     // vertical bar; Height acts as THICKNESS, segments stack top->bottom
    | Right

/// A bar/omnibox color: either a FIXED "#rrggbb"/"#rrggbbaa" hex, or a function
/// of the wallpaper palette (the SAME palette the borders read). Palette colors
/// are resolved host-side at snapshot time, so they track a dynamic wallpaper
/// live — the bar re-styles itself through the day. The wire stays plain hex.
type ColorSpec =
    | Fixed of string
    | OfPalette of (Palette.Palette -> string)

module ColorSpec =
    /// Set once the first time a palette function throws, so the diagnostic is
    /// emitted a single time per process rather than ~once/second per snapshot.
    let mutable private warnedThrow = false

    /// Resolve to a "#rrggbb(aa)" string. TOTAL: a throwing palette function
    /// degrades to the EMPTY string — deliberately NOT a valid hex, so the client
    /// (`parseHex ""` -> None -> `Option.defaultValue fallback`) keeps rendering
    /// its built-in default color and the element stays VISIBLE, rather than a
    /// valid-but-transparent "#00000000" that would silently hide it. The throw
    /// is logged once (observability: a fix isn't done without a trace) rather
    /// than swallowed on every snapshot.
    let resolve (pal: Palette.Palette) (c: ColorSpec) : string =
        match c with
        | Fixed s -> s
        | OfPalette f ->
            try
                f pal
            with ex ->
                if not warnedThrow then
                    warnedThrow <- true
                    eprintfn "WTF.Config: a palette color function threw (%s); \
                              the client keeps its default for it. Logged once." ex.Message
                ""

/// Colors are `ColorSpec` (a fixed hex OR a palette function). Multiple bars are
/// supported: give each a Name and launch one wtf-bar process per entry
/// (`wtf-bar` takes the first/only bar; `wtf-bar --name status2` the entry named
/// "status2").
type BarConfig =
    { Name: string
      Position: BarPosition
      Height: int              // thickness: bar height (Top/Bottom) or width (Left/Right)
      FontSize: float
      RefreshMs: int           // poll/redraw cadence; the bar only repaints when the
                               // visible content actually changed, so a small value
                               // buys responsiveness without a busy idle redraw
      Background: ColorSpec     // supports alpha (#rrggbbaa) for translucency
      Foreground: ColorSpec
      Dim: ColorSpec            // idle workspace / secondary text
      Accent: ColorSpec         // current-workspace pill / highlights
      Glass: bool               // frost the bar: scenefx backdrop blur behind it
      Embedded: bool            // render IN-PROCESS in the compositor (no separate
                                // wtf-bar process/poll); false = external client
      Left: BarSegment list
      Right: BarSegment list }

module BarConfig =
    /// The built-in look (what shipped before bar config existed).
    let defaults =
        { Name = "main"
          Position = Top
          Height = 28
          FontSize = 14.0
          RefreshMs = 300
          Background = Fixed "#1e1e2eeb"
          Foreground = Fixed "#cdd6f4"
          Dim = Fixed "#6c7086"
          Accent = Fixed "#89b4fa"
          Glass = false
          Embedded = true
          Left = [ Workspaces ]
          Right = [ Player; Network; Battery; Clock "HH:mm" ] }

type OmniboxConfig =
    { Width: int
      Height: int
      RowHeight: int
      FontSize: float
      Background: ColorSpec
      InputBackground: ColorSpec
      Foreground: ColorSpec
      Dim: ColorSpec
      Selection: ColorSpec     // selected-row background
      Prompt: string           // prompt glyph/text, e.g. ">" or "λ"
      PromptColor: ColorSpec
      Placeholder: string      // hint shown while the query is empty
      Glass: bool }            // frost the launcher: scenefx backdrop blur behind it

module OmniboxConfig =
    let defaults =
        { Width = 640
          Height = 400
          RowHeight = 30
          FontSize = 16.0
          Background = Fixed "#181825f4"
          InputBackground = Fixed "#313244"
          Foreground = Fixed "#cdd6f4"
          Dim = Fixed "#7f849c"
          Selection = Fixed "#89b4fa"
          Prompt = ">"
          PromptColor = Fixed "#a6e3a1"
          Placeholder = "type to search apps…"
          Glass = false }

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
      Watercolor: bool                  // appearance: watercolor window frames (border blurs+tints the backdrop)
      WatercolorTint: float             // watercolor border tint alpha over the blur (0..1; lower = clearer)
      WatercolorRefraction: float       // px the rim lenses the backdrop (0 = flat frost; ~6-14 = edge bend)
      WatercolorFrost: bool             // lens source: true = frosted (blurred), false = clear water-drop
      // --- Liquid Glass (#7): the richer rim effect layered on the watercolor
      // refraction shader. Off (Glass=false) => byte-identical to today. ---
      Glass: bool                       // appearance: enable the Liquid Glass rim effect
      GlassRefractionIndex: float       // refraction strength multiplier (1.0 = watercolor baseline; >1 bends more)
      GlassChromaticAberration: float   // rim colour fringing: px the R/B channels split along the normal (0 = off)
      GlassNoise: float                 // frosted micro-noise intensity 0..1 (0 = smooth)
      GlassSpecular: bool               // glossy specular crown highlight on the bead
      GlassSurface: string              // bead profile: convex_circle | convex_squircle | concave | lip
      Glow: bool                        // appearance: colored halo around the FOCUSED frame
      GlowSigma: float                  // glow spread in px
      GlowIntensity: float              // glow strength 0..1
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
      OpacityOf: Dyn<float> option      // per-window opacity (0..1, clamped on resolve)
      // --- E2 pluggable effect strategy (the name of a registered strategy in the
      // host's EffectRegistry; "none" => no extra per-window effects, byte-identical
      // to today). Resolution name -> strategy happens in the host, keeping this
      // field a plain string so Config.fs stays free of the Effect types. ---
      EffectStrategy: string }          // registered effect-strategy name (default "none")

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
          Watercolor = false
          WatercolorTint = 0.35
          WatercolorRefraction = 0.0
          WatercolorFrost = false
          Glass = false
          GlassRefractionIndex = 1.0
          GlassChromaticAberration = 0.0
          GlassNoise = 0.0
          GlassSpecular = true
          GlassSurface = "convex_circle"
          Glow = false
          GlowSigma = 20.0
          GlowIntensity = 0.6
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
          OpacityOf = None
          EffectStrategy = "none" }

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
    /// Select a pluggable effect strategy by name: `effectStrategy "dim-unfocused"`.
    /// The name is resolved against the host's EffectRegistry (built-in "none" plus
    /// any IWtfEffectPlugin strategies). Unknown names fall back to "none" at resolve.
    [<CustomOperation "effectStrategy">]
    member _.EffectStrategy(c, name: string) = { c with EffectStrategy = name }
    [<CustomOperation "cornerRadius">]
    member _.CornerRadius(c, v) = { c with CornerRadius = v }
    [<CustomOperation "blur">]
    member _.Blur(c, v) = { c with Blur = v }
    /// Watercolor window frames (scenefx): the border blurs the backdrop behind
    /// it, tinted translucent — a soft wash, not a glass slab. Best paired with
    /// `cornerRadius`. (The name `glass` is reserved for a future liquid-glass.)
    [<CustomOperation "watercolor">]
    member _.Watercolor(c, v) = { c with Watercolor = v }
    /// Watercolor border tint alpha over the blur (0..1; lower = clearer).
    [<CustomOperation "watercolorTint">]
    member _.WatercolorTint(c, v: float) = { c with WatercolorTint = v }
    /// Watercolor edge refraction: px the rim lenses the backdrop (0 = flat frost;
    /// ~6-14 = an edge-bend). Needs `watercolor` + a `cornerRadius`.
    [<CustomOperation "watercolorRefraction">]
    member _.WatercolorRefraction(c, v: float) = { c with WatercolorRefraction = v }
    /// Watercolor lens source: false (default) = clear "water-drop" that refracts
    /// the sharp backdrop; true = frosted (lens the blur).
    [<CustomOperation "watercolorFrost">]
    member _.WatercolorFrost(c, v) = { c with WatercolorFrost = v }
    /// Liquid Glass (#7): the richer rim effect over the refraction shader —
    /// index-scaled bend, chromatic aberration, noise, specular, surface profile.
    /// `glass true` turns it on; the knobs below tune it. Needs a `cornerRadius`.
    [<CustomOperation "glass">]
    member _.Glass(c, v) = { c with Glass = v }
    /// Refraction strength multiplier over the watercolor baseline (1.0 = same;
    /// >1 bends the backdrop harder at the rim).
    [<CustomOperation "glassRefractionIndex">]
    member _.GlassRefractionIndex(c, v: float) = { c with GlassRefractionIndex = v }
    /// Rim chromatic aberration: px the red/blue channels split along the edge
    /// normal (0 = off; ~1-4 = a visible colour fringe).
    [<CustomOperation "glassChromaticAberration">]
    member _.GlassChromaticAberration(c, v: float) = { c with GlassChromaticAberration = v }
    /// Frosted micro-noise intensity 0..1 (0 = smooth glass; higher = grainier).
    [<CustomOperation "glassNoise">]
    member _.GlassNoise(c, v: float) = { c with GlassNoise = v }
    /// Glossy specular crown highlight on the bead (true = lit, false = matte).
    [<CustomOperation "glassSpecular">]
    member _.GlassSpecular(c, v) = { c with GlassSpecular = v }
    /// Bead surface profile: "convex_circle" | "convex_squircle" | "concave" | "lip".
    [<CustomOperation "glassSurface">]
    member _.GlassSurface(c, v: string) = { c with GlassSurface = v }
    /// Focus glow: a colored halo around the FOCUSED window's frame, in the
    /// frame's own color (`activeBorder` drives the hue). "The frame emits light."
    [<CustomOperation "glow">]
    member _.Glow(c, v) = { c with Glow = v }
    /// Glow halo spread in px (like a blur sigma; bigger = softer, wider halo).
    [<CustomOperation "glowSigma">]
    member _.GlowSigma(c, v: float) = { c with GlowSigma = v }
    /// Glow strength 0..1 (alpha of the halo at its brightest).
    [<CustomOperation "glowIntensity">]
    member _.GlowIntensity(c, v: float) = { c with GlowIntensity = v }
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
    [<CustomOperation "refreshMs">]
    member _.RefreshMs(c: BarConfig, v: int) = { c with RefreshMs = v }
    // Each color knob takes EITHER a fixed hex string OR a palette function
    // `(fun p -> Color.toHexA 0.5 p.Base)` — overloaded so both read naturally.
    [<CustomOperation "background">]
    member _.Background(c: BarConfig, v: string) = { c with Background = Fixed v }
    member _.Background(c: BarConfig, v: Palette.Palette -> string) = { c with Background = OfPalette v }
    [<CustomOperation "foreground">]
    member _.Foreground(c: BarConfig, v: string) = { c with Foreground = Fixed v }
    member _.Foreground(c: BarConfig, v: Palette.Palette -> string) = { c with Foreground = OfPalette v }
    [<CustomOperation "dim">]
    member _.Dim(c: BarConfig, v: string) = { c with Dim = Fixed v }
    member _.Dim(c: BarConfig, v: Palette.Palette -> string) = { c with Dim = OfPalette v }
    [<CustomOperation "accent">]
    member _.Accent(c: BarConfig, v: string) = { c with Accent = Fixed v }
    member _.Accent(c: BarConfig, v: Palette.Palette -> string) = { c with Accent = OfPalette v }
    [<CustomOperation "glass">]
    member _.Glass(c: BarConfig, v: bool) = { c with Glass = v }
    [<CustomOperation "embedded">]
    member _.Embedded(c: BarConfig, v: bool) = { c with Embedded = v }
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
    // Color knobs: fixed hex OR palette function, same as the bar builder.
    [<CustomOperation "background">]
    member _.Background(c: OmniboxConfig, v: string) = { c with Background = Fixed v }
    member _.Background(c: OmniboxConfig, v: Palette.Palette -> string) = { c with Background = OfPalette v }
    [<CustomOperation "inputBackground">]
    member _.InputBackground(c: OmniboxConfig, v: string) = { c with InputBackground = Fixed v }
    member _.InputBackground(c: OmniboxConfig, v: Palette.Palette -> string) = { c with InputBackground = OfPalette v }
    [<CustomOperation "foreground">]
    member _.Foreground(c: OmniboxConfig, v: string) = { c with Foreground = Fixed v }
    member _.Foreground(c: OmniboxConfig, v: Palette.Palette -> string) = { c with Foreground = OfPalette v }
    [<CustomOperation "dim">]
    member _.Dim(c: OmniboxConfig, v: string) = { c with Dim = Fixed v }
    member _.Dim(c: OmniboxConfig, v: Palette.Palette -> string) = { c with Dim = OfPalette v }
    [<CustomOperation "selection">]
    member _.Selection(c: OmniboxConfig, v: string) = { c with Selection = Fixed v }
    member _.Selection(c: OmniboxConfig, v: Palette.Palette -> string) = { c with Selection = OfPalette v }
    [<CustomOperation "promptColor">]
    member _.PromptColor(c: OmniboxConfig, v: string) = { c with PromptColor = Fixed v }
    member _.PromptColor(c: OmniboxConfig, v: Palette.Palette -> string) = { c with PromptColor = OfPalette v }
    [<CustomOperation "prompt">]
    member _.Prompt(c: OmniboxConfig, v: string) = { c with Prompt = v }
    [<CustomOperation "placeholder">]
    member _.Placeholder(c: OmniboxConfig, v: string) = { c with Placeholder = v }
    [<CustomOperation "glass">]
    member _.Glass(c: OmniboxConfig, v: bool) = { c with Glass = v }

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

    /// A shell-command bar widget: poll `exec` every `intervalMs` ms and show its
    /// first stdout line. Sugar for `Script { Exec = exec; IntervalMs = intervalMs }`,
    /// e.g. `right [ script "~/bin/cpu.sh" 2000; Clock "HH:mm" ]`.
    let script (exec: string) (intervalMs: int) : BarSegment =
        Script { Exec = exec; IntervalMs = intervalMs }

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

    // `Custom` and `Script` widgets are resolved HERE, host-side, to plain label
    // text — so the wire stays data and the bar client needs no knowledge of them
    // (both arrive as a `{"label": …}` segment). `ctx` is the live read-model;
    // `resolveScript` reads the host's cached poller output for a Script.
    let private segmentJson (ctx: BarContext) (resolveScript: ScriptWidget -> string) (s: BarSegment) : JsonNode =
        let labelNode (text: string) =
            let o = JsonObject()
            o["label"] <- JsonValue.Create text
            o :> JsonNode
        match s with
        | Workspaces -> JsonValue.Create "workspaces" :> JsonNode
        | Battery -> JsonValue.Create "battery" :> JsonNode
        | Network -> JsonValue.Create "network" :> JsonNode
        | Player -> JsonValue.Create "player" :> JsonNode
        | Clock fmt ->
            let o = JsonObject()
            o["clock"] <- JsonValue.Create fmt
            o :> JsonNode
        | Label text -> labelNode text
        | Custom f -> labelNode (BarSegment.resolveCustom ctx f)
        | Script sw -> labelNode (resolveScript sw)

    let private barJson (pal: Palette.Palette) (ctx: BarContext) (resolveScript: ScriptWidget -> string) (bar: BarConfig) : JsonNode =
        let hex (c: ColorSpec) = ColorSpec.resolve pal c
        let b = JsonObject()
        b["name"] <- JsonValue.Create bar.Name
        b["position"] <- JsonValue.Create(
            match bar.Position with
            | Top -> "top" | Bottom -> "bottom" | Left -> "left" | Right -> "right")
        b["height"] <- JsonValue.Create bar.Height
        b["fontSize"] <- JsonValue.Create bar.FontSize
        b["refreshMs"] <- JsonValue.Create bar.RefreshMs
        b["background"] <- JsonValue.Create(hex bar.Background)
        b["foreground"] <- JsonValue.Create(hex bar.Foreground)
        b["dim"] <- JsonValue.Create(hex bar.Dim)
        b["accent"] <- JsonValue.Create(hex bar.Accent)
        b["left"] <- JsonArray(bar.Left |> List.map (segmentJson ctx resolveScript) |> Array.ofList)
        b["right"] <- JsonArray(bar.Right |> List.map (segmentJson ctx resolveScript) |> Array.ofList)
        b :> JsonNode

    /// The "ui" object for the snapshot: { bars = [...]; omnibox = {...} }. Colors
    /// are resolved against `pal` (the active wallpaper palette) HERE — the wire
    /// stays plain hex, so a `ColorSpec.OfPalette` re-resolves every snapshot and
    /// the bar/omnibox track a dynamic wallpaper without any client change.
    let json (pal: Palette.Palette) (ctx: BarContext) (resolveScript: ScriptWidget -> string) (bars: BarConfig list) (omnibox: OmniboxConfig) : JsonNode =
        let hex (c: ColorSpec) = ColorSpec.resolve pal c
        let o = JsonObject()
        o["width"] <- JsonValue.Create omnibox.Width
        o["height"] <- JsonValue.Create omnibox.Height
        o["rowHeight"] <- JsonValue.Create omnibox.RowHeight
        o["fontSize"] <- JsonValue.Create omnibox.FontSize
        o["background"] <- JsonValue.Create(hex omnibox.Background)
        o["inputBackground"] <- JsonValue.Create(hex omnibox.InputBackground)
        o["foreground"] <- JsonValue.Create(hex omnibox.Foreground)
        o["dim"] <- JsonValue.Create(hex omnibox.Dim)
        o["selection"] <- JsonValue.Create(hex omnibox.Selection)
        o["prompt"] <- JsonValue.Create omnibox.Prompt
        o["promptColor"] <- JsonValue.Create(hex omnibox.PromptColor)
        o["placeholder"] <- JsonValue.Create omnibox.Placeholder
        let ui = JsonObject()
        ui["bars"] <- JsonArray(bars |> List.map (barJson pal ctx resolveScript) |> Array.ofList)
        ui["omnibox"] <- o
        ui :> JsonNode
