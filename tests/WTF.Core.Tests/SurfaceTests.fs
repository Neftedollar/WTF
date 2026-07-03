module WTF.Core.Tests.SurfaceTests

// Unit tests for the pure SurfaceRegistry (2c) — the in-process surface-plugin
// registry the loader feeds and the host reads. Mirrors the layout Registry:
// register / lookup / last-wins on a name collision.

open Xunit
open WTF.Core

// Tiny in-test surface implementations (no compositor / no rendering needed).
type private TestBar(name: string, anchor: SurfaceAnchor, thickness: int) =
    interface IWtfBarPlugin with
        member _.Name = name
        member _.Anchor = anchor
        member _.Thickness = thickness
        member _.RefreshMs = 250
        member _.Render (_ctx: BarContext) (w: int) (h: int) = Array.zeroCreate (w * h * 4)

type private TestOverlay(name: string, result: OverlayKeyResult) =
    interface IWtfOverlayPlugin with
        member _.Name = name
        member _.Width = 200
        member _.Height = 120
        member _.Open() = ()
        member _.OnKey (_m: uint32) (_s: uint32) (_c: uint32) = result
        member _.Render (w: int) (h: int) = Array.zeroCreate (w * h * 4)

[<Fact>]
let ``registers and resolves a bar surface`` () =
    SurfaceRegistry.clear ()
    Assert.False(SurfaceRegistry.hasBar "b1")
    SurfaceRegistry.registerBar (TestBar("b1", AnchorTop, 30))
    Assert.True(SurfaceRegistry.hasBar "b1")
    match SurfaceRegistry.tryBar "b1" with
    | Some p ->
        Assert.Equal(AnchorTop, p.Anchor)
        Assert.Equal(30, p.Thickness)
        // Render takes explicit width/height and returns width*height*4 BGRA bytes.
        Assert.Equal(4 * 4 * 4, (p.Render BarContext.empty 4 4).Length)
    | None -> failwith "b1 should resolve"
    Assert.Equal<string list>([ "b1" ], SurfaceRegistry.barNames ())

[<Fact>]
let ``registers and resolves an overlay surface`` () =
    SurfaceRegistry.clear ()
    SurfaceRegistry.registerOverlay (TestOverlay("o1", OverlayClose))
    Assert.True(SurfaceRegistry.hasOverlay "o1")
    match SurfaceRegistry.tryOverlay "o1" with
    | Some p ->
        Assert.Equal(200, p.Width)
        Assert.Equal(OverlayClose, p.OnKey 0u 0u 0u)
    | None -> failwith "o1 should resolve"
    Assert.Equal(None, SurfaceRegistry.tryOverlay "missing")

[<Fact>]
let ``last registration wins on a name collision`` () =
    SurfaceRegistry.clear ()
    SurfaceRegistry.registerBar (TestBar("dup", AnchorTop, 10))
    SurfaceRegistry.registerBar (TestBar("dup", AnchorBottom, 20))
    match SurfaceRegistry.tryBar "dup" with
    | Some p ->
        Assert.Equal(AnchorBottom, p.Anchor)   // the second one
        Assert.Equal(20, p.Thickness)
    | None -> failwith "dup should resolve"
    // still a single entry under that name
    Assert.Equal<string list>([ "dup" ], SurfaceRegistry.barNames ())

[<Fact>]
let ``allBars lists every registered bar and clear empties the registry`` () =
    SurfaceRegistry.clear ()
    SurfaceRegistry.registerBar (TestBar("a", AnchorTop, 10))
    SurfaceRegistry.registerBar (TestBar("b", AnchorLeft, 12))
    Assert.Equal(2, (SurfaceRegistry.allBars ()).Length)
    SurfaceRegistry.clear ()
    Assert.Empty(SurfaceRegistry.allBars ())
    Assert.Empty(SurfaceRegistry.barNames ())
    Assert.Empty(SurfaceRegistry.overlayNames ())
