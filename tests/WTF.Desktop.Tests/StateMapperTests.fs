module WTF.Desktop.Tests.StateMapperTests

open System.Collections.Generic
open Xunit
open Tmds.DBus
open WTF.Desktop.Models
open WTF.Desktop.Clients

/// Build the raw a{sv} dictionary the way D-Bus delivers it: values BOXED as
/// their variant CLR types (bool, double, uint32, ObjectPath).
let private dict (pairs: (string * obj) list) : IDictionary<string, obj> =
    let d = Dictionary<string, obj>()
    for (k, v) in pairs do d[k] <- v
    d :> IDictionary<string, obj>

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

// -- raw a{sv} parsing (the typed [<Dictionary>] mapping was broken; these lock
//    the manual parse that reads the real UPower/NM values). --------------------

[<Fact>]
let ``parseBattery reads boxed UPower variants`` () =
    // Exactly the shape busctl shows: IsPresent=b, Percentage=d, State=u.
    let b = parseBattery (dict [ "IsPresent", box true
                                 "Percentage", box 99.0
                                 "State", box 4u ])
    Assert.True(b.Present)
    Assert.Equal(99.0, b.Percentage)
    Assert.Equal("fully-charged", b.State)

[<Fact>]
let ``parseBattery degrades on missing or wrong-typed fields`` () =
    // Empty dict -> safe defaults (this is the bug's failure mode made safe).
    let b0 = parseBattery (dict [])
    Assert.False(b0.Present)
    Assert.Equal(0.0, b0.Percentage)
    Assert.Equal("unknown", b0.State)
    // A wrong CLR type for a field is ignored, not thrown on.
    let b1 = parseBattery (dict [ "Percentage", box "not-a-number"; "IsPresent", box true ])
    Assert.True(b1.Present)
    Assert.Equal(0.0, b1.Percentage)

[<Fact>]
let ``parseNetwork reads state and a real primary connection path`` () =
    let n = parseNetwork (dict [ "State", box 70u
                                 "Connectivity", box 4u
                                 "PrimaryConnection", box (ObjectPath "/org/freedesktop/NetworkManager/ActiveConnection/2") ])
    Assert.Equal("connected-global", n.State)
    Assert.Equal("full", n.Connectivity)
    Assert.Equal(Some "/org/freedesktop/NetworkManager/ActiveConnection/2", n.Primary)

[<Fact>]
let ``parseNetwork treats the root path as no primary connection`` () =
    let n = parseNetwork (dict [ "State", box 20u
                                 "PrimaryConnection", box (ObjectPath "/") ])
    Assert.Equal("disconnected", n.State)
    Assert.Equal(None, n.Primary)
