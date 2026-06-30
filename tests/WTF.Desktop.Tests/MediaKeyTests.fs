module WTF.Desktop.Tests.MediaKeyTests

open Xunit
open FsCheck.Xunit
open WTF.Desktop.Models

// The 8 mapped keysyms; anything else must yield None and never throw.
let private mapped =
    set
        [ 0x1008FF11u; 0x1008FF12u; 0x1008FF13u; 0x1008FF14u
          0x1008FF15u; 0x1008FF16u; 0x1008FF17u; 0x1008FF31u ]

[<Property>]
let ``ofKeysym is total: never throws, None outside the 8 mapped keysyms`` (keysym: uint32) =
    match MediaKey.ofKeysym keysym with
    | Some _ -> mapped.Contains keysym
    | None -> not (mapped.Contains keysym)

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
