module WTF.Desktop.Tests.MediaKeyTests

open Xunit
open WTF.Desktop.Models

[<Theory>]
[<InlineData(0x1008FF11u, "VolDown")>]
[<InlineData(0x1008FF12u, "Mute")>]
[<InlineData(0x1008FF13u, "VolUp")>]
[<InlineData(0x1008FF14u, "PlayPause")>]
[<InlineData(0x1008FF15u, "Stop")>]
[<InlineData(0x1008FF16u, "Prev")>]
[<InlineData(0x1008FF17u, "Next")>]
[<InlineData(0x1008FF31u, "Pause")>]
let ``ofKeysym maps XF86Audio keysyms`` (keysym: uint32) (expected: string) =
    let action = MediaKey.ofKeysym keysym
    Assert.Equal(Some expected, action |> Option.map (fun a -> string a))

[<Theory>]
[<InlineData(0x61u)>]       // 'a'
[<InlineData(0xFF1Bu)>]     // Escape
[<InlineData(0x1008FF20u)>] // some other XF86 key, not media transport/volume
let ``ofKeysym returns None for non-media keys`` (keysym: uint32) =
    Assert.Equal(None, MediaKey.ofKeysym keysym)
