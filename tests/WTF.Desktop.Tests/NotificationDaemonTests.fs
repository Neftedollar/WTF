module WTF.Desktop.Tests.NotificationDaemonTests

open System
open System.Collections.Generic
open Xunit
open WTF.Desktop
open WTF.Desktop.Models
open WTF.Desktop.DBus

// The daemon is constructible with a bare Aggregator (no bus needed): we drive
// the IFreedesktopNotifications surface directly.
let private mk () =
    let agg = Aggregator()
    let d = new NotificationDaemon(agg)
    agg, d, (d :> IFreedesktopNotifications)

let private notify (iface: IFreedesktopNotifications) appName replaces summary body actions hints timeout =
    iface.NotifyAsync(appName, replaces, "icon", summary, body, actions, hints, timeout)
        .GetAwaiter().GetResult()

// Capture NotificationClosed signals into a list.
let private collectClosed (iface: IFreedesktopNotifications) =
    let got = ResizeArray<struct (uint32 * uint32)>()
    // The event payload is a ValueTuple, which F# treats as a "non-standard" event
    // type — subscribe via the generated explicit accessor.
    iface.add_NotificationClosed(Action<struct (uint32 * uint32)>(fun a -> got.Add a))
    got

// --- actions[] flat-pair parsing ---

[<Fact>]
let ``even-length actions array yields correct key,label pairs`` () =
    let agg, _, iface = mk ()
    let id = notify iface "app" 0u "s" "b" [| "default"; "Open"; "dismiss"; "Dismiss" |] null 0
    let n = (NotificationStore.tryFind id (agg.Snapshot().Notifications)).Value
    Assert.Equal<(string * string) list>([ ("default", "Open"); ("dismiss", "Dismiss") ], n.Actions)

[<Fact>]
let ``odd-length actions array drops the trailing lone key`` () =
    let agg, _, iface = mk ()
    let id = notify iface "app" 0u "s" "b" [| "a"; "A"; "lonely" |] null 0
    let n = (NotificationStore.tryFind id (agg.Snapshot().Notifications)).Value
    Assert.Equal<(string * string) list>([ ("a", "A") ], n.Actions)

[<Fact>]
let ``empty actions array yields no actions`` () =
    let agg, _, iface = mk ()
    let id = notify iface "app" 0u "s" "b" [||] null 0
    let n = (NotificationStore.tryFind id (agg.Snapshot().Notifications)).Value
    Assert.Empty(n.Actions)

[<Fact>]
let ``null actions array degrades to no actions instead of throwing`` () =
    let agg, _, iface = mk ()
    // Regression: actions.Length was dereferenced with no null guard.
    let id = notify iface "app" 0u "s" "b" null null 0
    let n = (NotificationStore.tryFind id (agg.Snapshot().Notifications)).Value
    Assert.Empty(n.Actions)

// --- urgency hint extraction ---

[<Fact>]
let ``urgency hint as a byte is extracted to Some`` () =
    let agg, _, iface = mk ()
    let hints = dict [ "urgency", box 2uy ] |> Dictionary
    let id = notify iface "app" 0u "s" "b" [||] hints 0
    Assert.Equal(Some 2uy, (NotificationStore.tryFind id (agg.Snapshot().Notifications)).Value.Urgency)

[<Fact>]
let ``urgency hint as an int convertible to byte is extracted`` () =
    let agg, _, iface = mk ()
    let hints = dict [ "urgency", box 1 ] |> Dictionary
    let id = notify iface "app" 0u "s" "b" [||] hints 0
    Assert.Equal(Some 1uy, (NotificationStore.tryFind id (agg.Snapshot().Notifications)).Value.Urgency)

[<Fact>]
let ``hints without urgency key yields None`` () =
    let agg, _, iface = mk ()
    let hints = dict [ "category", box "im" ] |> Dictionary
    let id = notify iface "app" 0u "s" "b" [||] hints 0
    Assert.Equal(None, (NotificationStore.tryFind id (agg.Snapshot().Notifications)).Value.Urgency)

[<Fact>]
let ``urgency hint with a non-convertible value yields None`` () =
    let agg, _, iface = mk ()
    let hints = dict [ "urgency", box "not-a-number" ] |> Dictionary
    let id = notify iface "app" 0u "s" "b" [||] hints 0
    Assert.Equal(None, (NotificationStore.tryFind id (agg.Snapshot().Notifications)).Value.Urgency)

[<Fact>]
let ``null hints dictionary yields None`` () =
    let agg, _, iface = mk ()
    let id = notify iface "app" 0u "s" "b" [||] null 0
    Assert.Equal(None, (NotificationStore.tryFind id (agg.Snapshot().Notifications)).Value.Urgency)

// --- CloseNotification + NotificationClosed event ---

[<Fact>]
let ``closing a present id removes it and raises NotificationClosed reason 3`` () =
    let agg, _, iface = mk ()
    let closed = collectClosed iface
    let id = notify iface "app" 0u "s" "b" [||] null 0
    iface.CloseNotificationAsync(id).GetAwaiter().GetResult()
    Assert.Empty(agg.Snapshot().Notifications.Active)
    Assert.Equal<struct (uint32 * uint32) list>([ struct (id, 3u) ], List.ofSeq closed)

[<Fact>]
let ``closing an absent id is a no-op and raises nothing`` () =
    let _, _, iface = mk ()
    let closed = collectClosed iface
    iface.CloseNotificationAsync(424242u).GetAwaiter().GetResult()
    Assert.Empty(closed)

// --- end-to-end via the Aggregator ---

[<Fact>]
let ``NotifyAsync returns the id the store allocates and is visible in Snapshot`` () =
    let agg, _, iface = mk ()
    let id = notify iface "Firefox" 0u "Hi" "there" [||] null 0
    Assert.Equal(1u, id)
    let active = agg.Snapshot().Notifications.Active
    Assert.Equal(1, active.Length)
    Assert.Equal("Firefox", active.Head.AppName)

[<Fact>]
let ``replacesId reuses the id end-to-end and overwrites content`` () =
    let agg, _, iface = mk ()
    let id = notify iface "app" 0u "old-summary" "old" [||] null 0
    let id2 = notify iface "app" id "new-summary" "new" [||] null 0
    Assert.Equal(id, id2)
    let active = agg.Snapshot().Notifications.Active
    Assert.Equal(1, active.Length)
    Assert.Equal("new-summary", active.Head.Summary)
    Assert.Equal("new", active.Head.Body)

// --- expiry tick -> NotificationClosed reason 1 ---

[<Fact>]
let ``Tick expires due notifications and emits NotificationClosed reason 1 earliest-first`` () =
    let agg, d, iface = mk ()
    let closed = collectClosed iface
    // Inject two already-expired notifications (past ExpiresAtMs) directly, so the
    // tick is deterministic regardless of the wall clock.
    agg.Update(fun s ->
        let mkN id exp =
            { Id = id; ReplacesId = 0u; AppName = "a"; AppIcon = ""; Summary = "s"; Body = "b"
              Actions = []; Urgency = None; CreatedMs = 0L; ExpiresAtMs = Some exp }
        { s with Notifications = { Active = [ mkN 1u 100L; mkN 2u 50L ]; NextId = 3u } })
    d.Tick()
    Assert.Empty(agg.Snapshot().Notifications.Active)
    // Earliest expiry (50 -> id 2) is reported before (100 -> id 1); reason = 1.
    Assert.Equal<struct (uint32 * uint32) list>(
        [ struct (2u, 1u); struct (1u, 1u) ], List.ofSeq closed)

[<Fact>]
let ``Tick with nothing due emits nothing and keeps survivors`` () =
    let agg, d, iface = mk ()
    let closed = collectClosed iface
    let _ = notify iface "app" 0u "s" "b" [||] null 0 // never expires
    d.Tick()
    Assert.Empty(closed)
    Assert.Equal(1, agg.Snapshot().Notifications.Active.Length)

// --- lifecycle: timer disposal ---

[<Fact>]
let ``double Start and Dispose do not throw (timer lifecycle)`` () =
    let agg = Aggregator()
    let d = new NotificationDaemon(agg)
    d.Start()
    d.Start() // must dispose the prior timer, not leak it
    (d :> IDisposable).Dispose()
    (d :> IDisposable).Dispose() // idempotent
