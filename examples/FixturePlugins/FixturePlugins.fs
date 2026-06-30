namespace FixturePlugins

// =============================================================================
// TEST-FIXTURE plugin assembly (NOT a shipped example). It exercises the loader's
// edge cases that the single SpiralLayout example can't:
//   * a plugin exposing MULTIPLE layouts (both must register),
//   * a layout name that COLLIDES with a built-in ("tall" — last wins),
//   * a plugin whose CONSTRUCTOR THROWS (must be skipped; others still load),
//   * a plugin type with NO parameterless ctor (must be skipped),
//   * a plugin whose Layouts list has an INTRA-plugin duplicate name.
// Mirrors SpiralLayout's <Private>false</Private> WTF.Core reference so only
// FixturePlugins.dll is emitted (shared host WTF.Core identity at load time).
// =============================================================================

open WTF.Core

module Fixtures =
    /// A distinctive marker layout: every window gets the SAME 7x7 rect at the
    /// origin — trivially distinguishable from any built-in layout's geometry, so
    /// a collision override is provable by the resolved layout's output.
    let markerFactory: LayoutFactory =
        fun _nmaster _ratio ->
            fun _area s -> Stack.toList s |> List.map (fun w -> w, Rect.create 0 0 7 7)

/// One plugin exposing MULTIPLE distinct layouts — the loader must register both.
type MultiPlugin() =
    interface IWtfLayoutPlugin with
        member _.Name = "MultiFixture"
        member _.Layouts =
            [ "fixture_alpha", Fixtures.markerFactory
              "fixture_beta", Fixtures.markerFactory ]

/// A plugin whose layout name collides with the built-in "tall" — last wins, so
/// after load Registry.resolve "tall" must yield THIS marker layout.
type OverrideTallPlugin() =
    interface IWtfLayoutPlugin with
        member _.Name = "OverrideTall"
        member _.Layouts = [ "tall", Fixtures.markerFactory ]

/// A plugin whose Layouts list contains the SAME name twice (intra-plugin
/// duplicate) — last entry wins; both register without crashing.
type IntraDuplicatePlugin() =
    interface IWtfLayoutPlugin with
        member _.Name = "IntraDuplicate"
        member _.Layouts =
            [ "fixture_dup", Fixtures.markerFactory
              "fixture_dup", Fixtures.markerFactory ]

/// A plugin whose CONSTRUCTOR THROWS — the loader must skip it (logged) while the
/// OTHER plugin types in the same assembly still register (per-type isolation).
type ThrowingPlugin() =
    do failwith "boom from plugin ctor"
    interface IWtfLayoutPlugin with
        member _.Name = "Throwing"
        member _.Layouts = [ "fixture_throwing_should_not_appear", Fixtures.markerFactory ]

/// A plugin with NO public parameterless constructor — the loader can't
/// instantiate it reflectively, so it must be skipped (now logged).
type NoDefaultCtorPlugin(unused: int) =
    member _.Unused = unused
    interface IWtfLayoutPlugin with
        member _.Name = "NoDefaultCtor"
        member _.Layouts = [ "fixture_noctor_should_not_appear", Fixtures.markerFactory ]
