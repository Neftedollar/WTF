module WTF.Core.Tests.EffectTests

// Unit tests for the pure EffectRegistry (#6) — the effect-strategy registry the
// loader feeds and the host resolves by the config's `effectStrategy` name.
// Mirrors the layout Registry / SurfaceRegistry: built-in "none", register /
// resolve, last-wins on a collision, and a TOTAL resolve that degrades to "none".

open Xunit
open WTF.Core

let private ctx focused appId : RenderContext =
    { Window = { Id = 1; AppId = appId; Title = ""; Floating = false }
      Workspace = "1"; Focused = focused; Palette = Palette.defaultPalette }

[<Fact>]
let ``built-in none is always present and yields no effects`` () =
    EffectRegistry.clear ()
    Assert.True(EffectRegistry.has "none")
    Assert.Contains("none", EffectRegistry.names ())
    Assert.Equal<WindowEffect list>([], EffectRegistry.resolve "none" (ctx true "foot"))

[<Fact>]
let ``registers and resolves a custom strategy`` () =
    EffectRegistry.clear ()
    EffectRegistry.register "dim" (fun c -> if c.Focused then [] else [ SetOpacity 0.4 ])
    Assert.True(EffectRegistry.has "dim")
    let dim = EffectRegistry.resolve "dim"
    Assert.Equal<WindowEffect list>([], dim (ctx true "x"))
    Assert.Equal<WindowEffect list>([ SetOpacity 0.4 ], dim (ctx false "x"))

[<Fact>]
let ``an unknown name resolves to none (never throws)`` () =
    EffectRegistry.clear ()
    Assert.Equal<WindowEffect list>([], EffectRegistry.resolve "does-not-exist" (ctx false "x"))

[<Fact>]
let ``last registration wins on a name collision`` () =
    EffectRegistry.clear ()
    EffectRegistry.register "s" (fun _ -> [ SetOpacity 0.1 ])
    EffectRegistry.register "s" (fun _ -> [ SetOpacity 0.9 ])
    Assert.Equal<WindowEffect list>([ SetOpacity 0.9 ], EffectRegistry.resolve "s" (ctx true "x"))

[<Fact>]
let ``clear re-seeds the built-in none and drops customs`` () =
    EffectRegistry.register "temp" (fun _ -> [ WindowEffect.SetBorderColor "#ffffff" ])
    Assert.True(EffectRegistry.has "temp")
    EffectRegistry.clear ()
    Assert.False(EffectRegistry.has "temp")
    // "none" survives clear() and stays the no-op identity.
    Assert.True(EffectRegistry.has "none")
    Assert.Equal<WindowEffect list>([], EffectRegistry.resolve "none" (ctx false "x"))
