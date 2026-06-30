module WTF.Desktop.Tests.VolumeTests

open System
open Xunit
open FsCheck.Xunit
open WTF.Desktop

[<Fact>]
let ``positive delta raises with +`` () =
    let wp, pa = Volume.formatArgs 5
    Assert.Equal("set-volume @DEFAULT_AUDIO_SINK@ 5%+", wp)
    Assert.Equal("set-sink-volume @DEFAULT_SINK@ +5%", pa)

[<Fact>]
let ``negative delta lowers with -`` () =
    let wp, pa = Volume.formatArgs -5
    Assert.Equal("set-volume @DEFAULT_AUDIO_SINK@ 5%-", wp)
    Assert.Equal("set-sink-volume @DEFAULT_SINK@ -5%", pa)

[<Fact>]
let ``zero delta is treated as a raise`` () =
    let wp, pa = Volume.formatArgs 0
    Assert.Equal("set-volume @DEFAULT_AUDIO_SINK@ 0%+", wp)
    Assert.Equal("set-sink-volume @DEFAULT_SINK@ +0%", pa)

[<Fact>]
let ``Int32.MinValue does not overflow (regression: abs would throw)`` () =
    // abs (Int32.MinValue) throws OverflowException; the int64 magnitude avoids it.
    let wp, pa = Volume.formatArgs Int32.MinValue
    Assert.Equal("set-volume @DEFAULT_AUDIO_SINK@ 2147483648%-", wp)
    Assert.Equal("set-sink-volume @DEFAULT_SINK@ -2147483648%", pa)

[<Property>]
let ``formatArgs never throws for any int`` (pct: int) =
    Volume.formatArgs pct |> ignore
    true
