module WTF.Core.Tests.AppearanceTests

open Xunit
open FsCheck.Xunit
open WTF.Core

// =====================================================================
//  E1 effects engine — the dynamic appearance model (RenderContext +
//  Dyn<_> border color / opacity). These pin THREE things:
//   (1) backward-compat: with no function configured (None), resolve
//       reproduces exactly the old focused?active:inactive behavior, and
//       the static activeBorder/... CE ops still drive it;
//   (2) the function form resolves correctly per app/floating/focus;
//   (3) resolveWindowStyle is pure + TOTAL (bad hex never throws,
//       opacity always clamped to [0,1]).
// =====================================================================

let private ctx app floating focused : RenderContext =
    { Window = { Id = 1; AppId = app; Title = ""; Floating = floating }
      Workspace = "1"
      Focused = focused
      Palette = Palette.defaultPalette }

let private cfg0 = WtfConfig.defaults

// ---- (1) backward compatibility: the None default == old behavior ----

[<Fact>]
let ``default (None) border == focused?active:inactive`` () =
    Assert.Equal(cfg0.ActiveBorder, Appearance.resolveBorderHex cfg0 (ctx "x" false true))
    Assert.Equal(cfg0.InactiveBorder, Appearance.resolveBorderHex cfg0 (ctx "x" false false))

[<Fact>]
let ``default (None) opacity == focused?1:Inactive`` () =
    Assert.Equal(1.0, Appearance.resolveOpacity cfg0 (ctx "x" false true), 6)
    Assert.Equal(cfg0.InactiveOpacity, Appearance.resolveOpacity cfg0 (ctx "x" false false), 6)

[<Fact>]
let ``static activeBorder CE still drives resolve (None falls through to live field)`` () =
    let cfg = config { activeBorder "#ff0000" }
    Assert.True(cfg.BorderColorOf.IsNone)
    Assert.Equal("#ff0000", Appearance.resolveBorderHex cfg (ctx "x" false true))

[<Fact>]
let ``static inactiveOpacity CE still drives resolve`` () =
    let cfg = config { inactiveOpacity 0.5 }
    Assert.True(cfg.OpacityOf.IsNone)
    Assert.Equal(0.5, Appearance.resolveOpacity cfg (ctx "x" false false), 6)

// ---- Liquid Glass (#7) config surface: off by default (byte-identical to
//      today), and each CE knob writes its field. The advanced knobs are inert
//      until the scenefx GLSL patch grows them, but the config must carry them. ----

[<Fact>]
let ``Liquid Glass defaults to off with an identity refraction index`` () =
    Assert.False(cfg0.Glass)
    Assert.Equal(1.0, cfg0.GlassRefractionIndex, 6)
    Assert.Equal(0.0, cfg0.GlassChromaticAberration, 6)
    Assert.Equal(0.0, cfg0.GlassNoise, 6)
    Assert.True(cfg0.GlassSpecular)
    Assert.Equal("convex_circle", cfg0.GlassSurface)

[<Fact>]
let ``glass CE ops write their fields`` () =
    let cfg =
        config {
            glass true
            glassRefractionIndex 1.4
            glassChromaticAberration 2.0
            glassNoise 0.3
            glassSpecular false
            glassSurface "lip"
        }
    Assert.True(cfg.Glass)
    Assert.Equal(1.4, cfg.GlassRefractionIndex, 6)
    Assert.Equal(2.0, cfg.GlassChromaticAberration, 6)
    Assert.Equal(0.3, cfg.GlassNoise, 6)
    Assert.False(cfg.GlassSpecular)
    Assert.Equal("lip", cfg.GlassSurface)

// ---- (2) the function form resolves per context ----

[<Fact>]
let ``borderColor function resolves per app/floating/focus`` () =
    let cfg =
        config {
            borderColor (fun w ->
                if   w.Window.AppId = "firefox" then "#ff8800"
                elif w.Window.Floating          then "#ff00ff"
                elif w.Focused                  then "#89b4fa"
                else                                 "#45475a")
        }
    Assert.Equal("#ff8800", Appearance.resolveBorderHex cfg (ctx "firefox" false false))
    Assert.Equal("#ff00ff", Appearance.resolveBorderHex cfg (ctx "foot" true false))
    Assert.Equal("#89b4fa", Appearance.resolveBorderHex cfg (ctx "foot" false true))
    Assert.Equal("#45475a", Appearance.resolveBorderHex cfg (ctx "foot" false false))

[<Fact>]
let ``windowOpacity function resolves and clamps to [0,1]`` () =
    let cfg = config { windowOpacity (fun w -> if w.Window.AppId = "foot" then 0.85 else 1.0) }
    Assert.Equal(0.85, Appearance.resolveOpacity cfg (ctx "foot" false true), 6)
    Assert.Equal(1.0, Appearance.resolveOpacity cfg (ctx "firefox" false true), 6)
    // totality of the clamp: out-of-range results are pinned to the [0,1] edges.
    let hi = config { windowOpacity (fun _ -> 2.0) }
    let lo = config { windowOpacity (fun _ -> -1.0) }
    Assert.Equal(1.0, Appearance.resolveOpacity hi (ctx "x" false false), 6)
    Assert.Equal(0.0, Appearance.resolveOpacity lo (ctx "x" false false), 6)

[<Fact>]
let ``resolveWindowStyle parses hex to rgb`` () =
    let cfg = config { borderColor (Dyn.constant "#89b4fa") }
    let style = Appearance.resolveWindowStyle cfg (ctx "x" false true)
    let (r, g, b) = style.BorderColor
    Assert.Equal(0.537, r, 2)
    Assert.Equal(0.706, g, 2)
    Assert.Equal(0.984, b, 2)

[<Fact>]
let ``bad hex is total, falls back to InactiveBorder rgb, never throws`` () =
    let cfg = config { borderColor (Dyn.constant "zzz") }
    let style = Appearance.resolveWindowStyle cfg (ctx "x" false true)
    let expected = (Protocol.hexColor cfg0.InactiveBorder).Value
    Assert.Equal(expected, style.BorderColor)

[<Fact>]
let ``Dyn.constant ignores context`` () =
    let d = Dyn.constant "#fff"
    Assert.Equal("#fff", d (ctx "a" true true))
    Assert.Equal("#fff", d (ctx "b" false false))

[<Fact>]
let ``Dyn.map composes over the result`` () =
    let d = Dyn.constant 3 |> Dyn.map (fun n -> n * 2)
    Assert.Equal(6, d (ctx "x" false false))

// ---- (3) FsCheck properties: totality + the backward-compat equivalence ----

// Totality over a small family of opacity functions: a constant `c`, and a
// focused?a:b split. Whatever arbitrary floats the function returns, the resolved
// opacity must always land in [0,1] (the clamp is total).
[<Property>]
let ``resolveOpacity clamps a constant function into [0,1]`` (c: float) (app: string) (floating: bool) (focused: bool) =
    let cfg = { cfg0 with OpacityOf = Some(Dyn.constant c) }
    let o = Appearance.resolveOpacity cfg (ctx app floating focused)
    o >= 0.0 && o <= 1.0

[<Property>]
let ``resolveOpacity clamps a focused-split function into [0,1]`` (a: float) (b: float) (app: string) (floating: bool) (focused: bool) =
    let f (rc: RenderContext) = if rc.Focused then a else b
    let cfg = { cfg0 with OpacityOf = Some f }
    let o = Appearance.resolveOpacity cfg (ctx app floating focused)
    o >= 0.0 && o <= 1.0

[<Property>]
let ``resolveWindowStyle opacity always in [0,1] and total`` (app: string) (title: string) (floating: bool) (focused: bool) =
    let c : RenderContext = { Window = { Id = 1; AppId = app; Title = title; Floating = floating }; Workspace = "1"; Focused = focused; Palette = Palette.defaultPalette }
    let style = Appearance.resolveWindowStyle cfg0 c
    style.Opacity >= 0.0 && style.Opacity <= 1.0

[<Property>]
let ``with both overrides None, opacity == focused?1:Inactive for all ctx`` (app: string) (floating: bool) (focused: bool) =
    let o = Appearance.resolveOpacity cfg0 (ctx app floating focused)
    let expected = if focused then 1.0 else cfg0.InactiveOpacity
    o = expected
