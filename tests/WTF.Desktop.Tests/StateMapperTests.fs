module WTF.Desktop.Tests.StateMapperTests

open Xunit
open WTF.Desktop.Models

[<Theory>]
[<InlineData(1u, "charging")>]
[<InlineData(2u, "discharging")>]
[<InlineData(3u, "empty")>]
[<InlineData(4u, "fully-charged")>]
[<InlineData(5u, "pending-charge")>]
[<InlineData(6u, "pending-discharge")>]
[<InlineData(0u, "unknown")>]
[<InlineData(99u, "unknown")>]
let ``upowerStateLabel maps known codes`` (code: uint32) (expected: string) =
    Assert.Equal(expected, upowerStateLabel code)

[<Theory>]
[<InlineData(10u, "asleep")>]
[<InlineData(20u, "disconnected")>]
[<InlineData(30u, "disconnecting")>]
[<InlineData(40u, "connecting")>]
[<InlineData(50u, "connected-local")>]
[<InlineData(60u, "connected-site")>]
[<InlineData(70u, "connected-global")>]
[<InlineData(0u, "unknown")>]
[<InlineData(5u, "unknown")>]
let ``nmStateLabel maps known codes`` (code: uint32) (expected: string) =
    Assert.Equal(expected, nmStateLabel code)

[<Theory>]
[<InlineData(1u, "none")>]
[<InlineData(2u, "portal")>]
[<InlineData(3u, "limited")>]
[<InlineData(4u, "full")>]
[<InlineData(0u, "unknown")>]
[<InlineData(9u, "unknown")>]
let ``nmConnectivityLabel maps known codes`` (code: uint32) (expected: string) =
    Assert.Equal(expected, nmConnectivityLabel code)

[<Fact>]
let ``PowerState.initial is awake and unlocked`` () =
    Assert.False(PowerState.initial.PreparingForSleep)
    Assert.False(PowerState.initial.SessionLocked)

[<Fact>]
let ``DesktopState.empty has no devices and an empty store`` () =
    let s = DesktopState.empty
    Assert.Equal(None, s.Battery)
    Assert.Equal(None, s.Network)
    Assert.Empty(s.Players)
    Assert.Empty(s.Notifications.Active)
    Assert.Equal(PowerState.initial, s.Power)
