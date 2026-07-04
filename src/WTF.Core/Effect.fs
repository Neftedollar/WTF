namespace WTF.Core

// =============================================================================
// Pluggable EFFECT strategies (#6) — the third welding point of the ".NET as a
// platform" plugin base, siblings to `IWtfLayoutPlugin` (layouts) and
// `IWtfBarPlugin`/`IWtfOverlayPlugin` (surfaces).
//
// An effect STRATEGY is a pure function `RenderContext -> Effect list`: given one
// window in one render context (its info, workspace, focus, palette) it decides
// which per-window visual effects apply. The host resolves the config's chosen
// strategy name against `EffectRegistry` and applies the returned effects on top
// of the existing static appearance, per window, every restyle.
//
// HONEST SCOPE: atomic GPU effects (blur, shadow, corner radius) are fixed in
// C/scenefx and NOT expressed here. What is pluggable is the *composition and
// targeting* of the per-window primitives the host can already drive per id:
// opacity (`wtf_set_window_opacity`) and border color (`wtf_set_window_border_color`).
// The DU is intentionally small and additive — a new primitive arrives as a new
// case here plus a host arm, never by changing the plugin ABI.
//
// FROZEN-ABI DISCIPLINE (see Plugin.fs / Surface.fs): `IWtfEffectPlugin` is frozen;
// a new capability arrives as ANOTHER interface, never a new member, so an
// already-compiled plugin never stops satisfying the contract.
//
// CORE STAYS PURE: these are just types + a pure strategy function + a process-
// global registry. No IO. The host owns the FFI that turns an `Effect` into pixels.
//
// Compiled AFTER Config.fs because `EffectStrategy` is `RenderContext -> _`, and
// `RenderContext` lives in Config.fs.
// =============================================================================

/// One per-window visual effect a strategy can request. Small and additive: each
/// case maps to a single host FFI arm. Values are renderer-ready (already the
/// units the FFI wants); the host clamps defensively. NAMED `WindowEffect` (not
/// `Effect`) to stay distinct from the reducer's `Effect` (Arrange/Spawn/…).
type WindowEffect =
    | SetOpacity of float          // window opacity 0..1 (host clamps)
    | SetBorderColor of string     // border color as a #hex (host parses; bad hex ignored)

/// A pure per-window effect decision: given the render context for one window,
/// return the effects that apply to it (empty = leave the static appearance
/// untouched, i.e. today's behavior).
type EffectStrategy = RenderContext -> WindowEffect list

/// The live registry of effect strategies — mirrors `Registry` (layouts) and
/// `SurfaceRegistry`. PURE + process-global; the loader registers plugin
/// strategies at startup, the host resolves the config's chosen name. The
/// built-in "none" strategy (no effects) is always present so an unknown/absent
/// name degrades to today's behavior rather than failing. Not thread-safe by
/// design: registration is a one-shot startup phase before the compositor loop.
module EffectRegistry =

    let private strategies = System.Collections.Generic.Dictionary<string, EffectStrategy>()

    /// The always-present identity strategy: no per-window effects. Keeping this a
    /// named constant lets the host treat "none" as the guaranteed fallback.
    let none: EffectStrategy = fun _ -> []

    // Seed the built-in. Mirrors World.Registry seeding its built-in layouts.
    do strategies["none"] <- none

    /// True if a strategy is already registered under `name` (loader warns).
    let has name = strategies.ContainsKey name

    /// Register (or replace) a strategy. Last-registered wins on a name collision.
    let register name (s: EffectStrategy) = strategies[name] <- s

    /// Resolve a name to its strategy, or `none` if unknown — TOTAL, never throws.
    let resolve name : EffectStrategy =
        match strategies.TryGetValue name with
        | true, s -> s
        | _ -> none

    let names () = strategies.Keys |> List.ofSeq |> List.sort

    /// Drop every registration except the built-in "none" (used by tests).
    let clear () =
        strategies.Clear()
        strategies["none"] <- none

/// An in-process effect plugin: contributes named strategies to `EffectRegistry`.
/// Discovered by the SAME reflective `PluginLoader` scan as layouts/surfaces
/// (`IsAssignableFrom`). Frozen ABI — one member, a name + its strategies.
type IWtfEffectPlugin =
    /// Human-readable plugin name (for logging; not the registry key).
    abstract member Name: string
    /// The strategies this plugin contributes: (registry-name, strategy) pairs.
    abstract member Strategies: (string * EffectStrategy) list
