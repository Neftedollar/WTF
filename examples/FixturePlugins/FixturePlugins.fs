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

// --- surface plugins (2c): the loader must discover IWtfBarPlugin /
//     IWtfOverlayPlugin in the SAME scan and feed SurfaceRegistry. ------------

/// A minimal in-process BAR surface: a solid strip. Proves IWtfBarPlugin is
/// discovered + registered into SurfaceRegistry.
type BarSurfacePlugin() =
    interface IWtfBarPlugin with
        member _.Name = "fixture_bar"
        member _.Anchor = AnchorBottom
        member _.Thickness = 24
        member _.RefreshMs = 500
        member _.Render (_ctx: BarContext) (w: int) (h: int) = Array.zeroCreate (max 0 (w * h * 4))

/// A minimal OVERLAY surface: fixed size, dismisses on any key.
type OverlaySurfacePlugin() =
    interface IWtfOverlayPlugin with
        member _.Name = "fixture_overlay"
        member _.Width = 100
        member _.Height = 50
        member _.Open() = ()
        member _.OnKey (_mods: uint32) (_sym: uint32) (_cp: uint32) = OverlayClose
        member _.Render (w: int) (h: int) = Array.zeroCreate (max 0 (w * h * 4))

/// A type that is BOTH a layout AND a bar — the loader registers the SINGLE
/// instance into both registries.
type DualLayoutBarPlugin() =
    interface IWtfLayoutPlugin with
        member _.Name = "DualLB"
        member _.Layouts = [ "fixture_dual", Fixtures.markerFactory ]
    interface IWtfBarPlugin with
        member _.Name = "fixture_dual_bar"
        member _.Anchor = AnchorTop
        member _.Thickness = 10
        member _.RefreshMs = 1000
        member _.Render (_ctx: BarContext) (w: int) (h: int) = Array.zeroCreate (max 0 (w * h * 4))

// --- effect plugins (#6): the loader must discover IWtfEffectPlugin in the SAME
//     scan and feed its strategies into EffectRegistry. ------------------------

/// A plugin exposing MULTIPLE named strategies — both must register. Strategies
/// are distinctive: one dims unfocused windows, one colors by app id.
type EffectPlugin() =
    interface IWtfEffectPlugin with
        member _.Name = "FixtureEffects"
        member _.Strategies =
            [ "fixture_dim", (fun ctx -> if ctx.Focused then [] else [ SetOpacity 0.5 ])
              "fixture_paint",
                (fun ctx ->
                    if ctx.Window.AppId = "firefox" then [ WindowEffect.SetBorderColor "#ff8800" ] else []) ]

/// A plugin whose strategy name COLLIDES with the built-in "none" — last wins, so
/// after load EffectRegistry.resolve "none" yields THIS strategy (marks every
/// window opaque-red), proving the override + warning path.
type OverrideNoneEffectPlugin() =
    interface IWtfEffectPlugin with
        member _.Name = "OverrideNone"
        member _.Strategies = [ "none", (fun _ -> [ WindowEffect.SetBorderColor "#ff0000" ]) ]

// --- workspace-type plugins (#5): the loader must discover IWtfWorkspacePlugin in
//     the SAME scan and feed WorkspaceRegistry. -------------------------------

/// A plugin exposing MULTIPLE distinctive workspace types. "fixture_focus_only"
/// places ONLY the focused window full-screen (proving a type reads the REAL focus
/// and controls visibility by omitting the rest — the host hides them). "fixture_all"
/// stacks every window at the origin (a trivial always-visible marker).
type WorkspacePlugin() =
    interface IWtfWorkspacePlugin with
        member _.Name = "FixtureWorkspaces"
        member _.WorkspaceTypes =
            [ "fixture_focus_only",
                (fun (v: WorkspaceView) ->
                    match v.Stack with
                    | Some s -> [ s.Focus, v.Screen ]   // only the focused id, full screen
                    | None -> [])
              "fixture_all",
                (fun (v: WorkspaceView) ->
                    match v.Stack with
                    | Some s -> Stack.toList s |> List.map (fun id -> id, Rect.create 0 0 5 5)
                    | None -> []) ]

/// A plugin whose type name COLLIDES with the built-in "stack" — last wins, so
/// after load WorkspaceRegistry.tryResolve "stack" yields THIS marker (every window
/// at 3x3@0,0), proving the override reaches the LIVE registry.
type OverrideStackPlugin() =
    interface IWtfWorkspacePlugin with
        member _.Name = "OverrideStack"
        member _.WorkspaceTypes =
            [ "stack",
                (fun (v: WorkspaceView) ->
                    match v.Stack with
                    | Some s -> Stack.toList s |> List.map (fun id -> id, Rect.create 0 0 3 3)
                    | None -> []) ]
