// Joins the "WorkspaceRegistry" collection: the byte-exact snapshot below reads
// the global WorkspaceRegistry (`workspaceTypes`), so it must not run in parallel
// with WorkspaceTypeTests, which register/clear types.
[<Xunit.Collection("WorkspaceRegistry")>]
module WTF.Core.Tests.GeometryTests

open Xunit
open FsCheck.Xunit
open WTF.Core

// =====================================================================
//  Units of Measure: the brain works in logical px (`lpx`); physical px
//  (`ppx`) exist only as a transient at the HiDPI scaling boundary. These
//  tests pin (a) byte-identity of the wire format at scale 1.0, (b) the
//  lpx<->ppx conversions, (c) the public `scale` config knob, and (d) that
//  edge-scaling preserves exact tiling (no HiDPI seams).
// =====================================================================

// ---- (1a) identity strip: scale 1.0 is a pure measure-strip ----------

[<Property>]
let ``configure 1.0 is an identity strip`` (x: int) (y: int) (w: int) (h: int) =
    let r = Rect.create x y w h
    Scaling.configure 1.0 r = (int r.X, int r.Y, int r.Width, int r.Height)

// ---- (1b) golden wire-format guard: byte-for-byte snapshot ----------
// A fixed 2-window world must serialise to exactly this string. Any drift
// in the JSON (e.g. a measure leaking into JsonValue.Create overload
// resolution, or a stray field) breaks this immediately.

[<Fact>]
let ``snapshotLine is byte-identical to the pre-UoM baseline`` () =
    let w =
        Reducer.applyMany
            [ AddWindow { Id = 1; AppId = "foot"; Title = "shell"; Floating = false }
              AddWindow { Id = 2; AppId = "firefox"; Title = "web"; Floating = false } ]
            (World.empty (Rect.create 0 0 1920 1080))
        |> fst
    let expected =
        """{"current":"1","nmaster":1,"ratio":0.5,"gaps":6,"screen":{"x":0,"y":0,"w":1920,"h":1080},"layouts":["bsp","full","grid","tall","wide"],"workspaceTypes":["stack"],"workspaces":[{"tag":"1","layout":"tall","type":"stack","state":"","windows":[2,1],"focused":2,"floating":[],"fullscreen":null},{"tag":"2","layout":"tall","type":"stack","state":"","windows":[],"focused":null,"floating":[],"fullscreen":null},{"tag":"3","layout":"tall","type":"stack","state":"","windows":[],"focused":null,"floating":[],"fullscreen":null},{"tag":"4","layout":"tall","type":"stack","state":"","windows":[],"focused":null,"floating":[],"fullscreen":null},{"tag":"5","layout":"tall","type":"stack","state":"","windows":[],"focused":null,"floating":[],"fullscreen":null},{"tag":"6","layout":"tall","type":"stack","state":"","windows":[],"focused":null,"floating":[],"fullscreen":null},{"tag":"7","layout":"tall","type":"stack","state":"","windows":[],"focused":null,"floating":[],"fullscreen":null},{"tag":"8","layout":"tall","type":"stack","state":"","windows":[],"focused":null,"floating":[],"fullscreen":null},{"tag":"9","layout":"tall","type":"stack","state":"","windows":[],"focused":null,"floating":[],"fullscreen":null}],"windows":{"1":{"appId":"foot","title":"shell","floating":false},"2":{"appId":"firefox","title":"web","floating":false}},"arrange":[{"id":2,"x":6,"y":6,"w":948,"h":1068},{"id":1,"x":966,"y":6,"w":948,"h":1068}]}"""
    Assert.Equal(expected, Protocol.snapshotLine w)

// ---- (2) lpx <-> ppx conversion ------------------------------------

[<Fact>]
let ``toPhysical and toLogical scale and round-trip`` () =
    Assert.Equal(200<ppx>, Px.toPhysical 2.0 100<lpx>)
    Assert.Equal(100<lpx>, Px.toLogical 2.0 200<ppx>)
    // round-trip at 1.0 is the identity
    Assert.Equal(137<lpx>, Px.toLogical 1.0 (Px.toPhysical 1.0 137<lpx>))
    // fractional scale rounds to nearest
    Assert.Equal(150<ppx>, Px.toPhysical 1.5 100<lpx>)

// ---- (3) the public `scale` config knob ----------------------------

[<Fact>]
let ``scale config knob defaults to 1.0 and is settable`` () =
    Assert.Equal(1.0, WtfConfig.defaults.Scale)
    Assert.Equal(1.5, (config { scale 1.5 }).Scale)
    // unrelated knobs are unaffected; default config stays logical
    Assert.Equal(1.0, (config { gaps 8 }).Scale)

// ---- (4) no behaviour change: tall under lpx matches plain ints -----

[<Property>]
let ``configure 1.0 over tall equals the raw layout ints`` (s: Stack<int>) =
    let screen = Rect.create 0 0 1920 1080
    Layout.tall 1 0.5 screen s
    |> List.forall (fun (_, r) ->
        let x, y, w, h = Scaling.configure 1.0 r
        x = int r.X && y = int r.Y && w = int r.Width && h = int r.Height)

// ---- (5) edge-scaled tiling: integer scale preserves abutment -------
// For an integer scale, edge-then-subtract is exact: configure s r =
// (x*s, y*s, w*s, h*s). That linearity is precisely what keeps adjacent
// tiles abutting with no 1px HiDPI gap or overlap.

[<Property>]
let ``integer-scale configure is exactly linear`` (x: int) (y: int) (w: int) (h: int) (k: int) =
    let s = 1 + (abs k % 4)            // integer scale in 1..4
    let r = Rect.create x y w h
    Scaling.configure (float s) r = (int r.X * s, int r.Y * s, int r.Width * s, int r.Height * s)

[<Property>]
let ``adjacent split tiles still abut after integer scaling`` (k: int) =
    let s = 1 + (abs k % 4)
    let area = Rect.create 0 0 1920 1080
    let left, right = Rect.splitVertical 0.5 area
    let lx, _, lw, _ = Scaling.configure (float s) left
    let rx, _, _, _ = Scaling.configure (float s) right
    lx + lw = rx                       // left's right edge == right's left edge
