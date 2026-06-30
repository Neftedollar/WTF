module WTF.Core.Tests.WallpaperTests

// The wallpaper module is best-effort and FFI-bound, but its pure helpers are
// exposed `internal` so they can be covered without a compositor or native lib:
//   - resizeModeOf maps every WallpaperMode (incl. the Tile->Crop fallback)
//   - expand handles "~"/"~/foo"/absolute (and the documented "~user" gap)
//   - pushImage fails closed for non-positive sizes or an unreadable path,
//     never touching the FFI.

open System
open SixLabors.ImageSharp.Processing
open Xunit
open WTF.Core
open WTF.Host

[<Theory>]
[<InlineData(0, 100)>]
[<InlineData(100, 0)>]
[<InlineData(-1, 100)>]
[<InlineData(100, -5)>]
[<InlineData(0, 0)>]
let ``pushImage returns false for non-positive sizes without touching disk or FFI`` (w: int) (h: int) =
    // A path that does not exist; the size guard must short-circuit before any load.
    Assert.False(Wallpaper.pushImage "/nonexistent/never.png" Fill w h)

[<Fact>]
let ``pushImage returns false for an unreadable path`` () =
    // Positive size but the file cannot be loaded -> false (logged), no FFI call.
    Assert.False(Wallpaper.pushImage "/nonexistent/zzz-no-such-file.png" Fill 64 64)

[<Theory>]
[<InlineData(0, 100)>]
[<InlineData(100, 0)>]
let ``pushImage size guard fires even for an unreadable path`` (w: int) (h: int) =
    Assert.False(Wallpaper.pushImage "/nonexistent/zzz.png" Stretch w h)

[<Fact>]
let ``resizeModeOf maps each WallpaperMode including the Tile fallback`` () =
    Assert.Equal(ResizeMode.Crop, Wallpaper.resizeModeOf Fill)
    Assert.Equal(ResizeMode.Pad, Wallpaper.resizeModeOf Fit)
    Assert.Equal(ResizeMode.Stretch, Wallpaper.resizeModeOf Stretch)
    Assert.Equal(ResizeMode.BoxPad, Wallpaper.resizeModeOf Center)
    Assert.Equal(ResizeMode.Crop, Wallpaper.resizeModeOf Tile)   // documented fallback

let private home = Environment.GetFolderPath Environment.SpecialFolder.UserProfile

[<Fact>]
let ``expand turns a bare tilde into the home directory`` () =
    Assert.Equal(home, Wallpaper.expand "~")

[<Fact>]
let ``expand replaces a leading tilde-slash with the home directory`` () =
    Assert.Equal(home + "/pics/bg.png", Wallpaper.expand "~/pics/bg.png")

[<Fact>]
let ``expand leaves an absolute path unchanged`` () =
    Assert.Equal("/usr/share/bg.png", Wallpaper.expand "/usr/share/bg.png")

[<Fact>]
let ``expand leaves a tilde-free relative path unchanged`` () =
    Assert.Equal("pics/bg.png", Wallpaper.expand "pics/bg.png")

[<Fact>]
let ``expand passes tilde-username through unchanged (documented limitation)`` () =
    // KNOWN minor gap: "~user/..." is NOT a home it can resolve, so it is passed
    // through literally and the load later fails closed. Pinned so a future fix is
    // a deliberate, test-visible change rather than silent.
    Assert.Equal("~alice/pic.png", Wallpaper.expand "~alice/pic.png")
