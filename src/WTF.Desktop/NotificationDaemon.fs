namespace WTF.Desktop

open System
open System.Threading
open System.Threading.Tasks
open System.Collections.Generic
open Tmds.DBus
open WTF.Desktop.Models
open WTF.Desktop.DBus

/// The `org.freedesktop.Notifications` server object. State lives in the pure
/// `NotificationStore` inside the `Aggregator`; this class is just the bus shim:
/// it parses the wire args, mutates the store under the aggregator lock, logs to
/// stderr, and raises the `NotificationClosed` / `ActionInvoked` signals. A ~1s
/// `Timer` drives expiry (reason = 1); `CloseNotification` raises reason = 3.
/// On-screen rendering is deferred (needs a surface — Phase 4); for #8 we own the
/// name, store, emit, and expose the live list via the agent snapshot.
type NotificationDaemon(agg: Aggregator) =

    /// Server default expiry when a client passes timeout = -1 (ms).
    let defaultTimeoutMs = 5000

    let closedEvt = DelegateEvent<Action<struct (uint32 * uint32)>>()
    let invokedEvt = DelegateEvent<Action<struct (uint32 * string)>>()

    let now () = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()

    let raiseClosed (id: uint32) (reason: uint32) =
        closedEvt.Trigger([| box (struct (id, reason)) |])

    // ~1s tick: expire due notifications and emit NotificationClosed(id, 1).
    let mutable timer: Timer = null

    let tick () =
        let removed =
            agg.Mutate(fun s ->
                let store', ids = NotificationStore.expire (now ()) s.Notifications
                { s with Notifications = store' }, ids)
        for id in removed do
            raiseClosed id 1u

    /// Start the expiry timer (called once, after the name is owned).
    member _.Start() =
        timer <- new Timer(TimerCallback(fun _ -> try tick () with _ -> ()), null, 1000, 1000)

    /// Emit ActionInvoked — a future bar/omnibox calls this when the user clicks
    /// a notification action. Exposed now so the wiring is in place.
    member _.RaiseAction(id: uint32, key: string) =
        invokedEvt.Trigger([| box (struct (id, key)) |])

    interface IDBusObject with
        member _.ObjectPath = ObjectPath "/org/freedesktop/Notifications"

    interface IFreedesktopNotifications with
        member _.NotifyAsync(appName, replacesId, appIcon, summary, body, actions, hints, expireTimeout) =
            // actions[] is a flat [key, label, key, label, ...] array.
            let acts =
                [ let mutable i = 0
                  while i + 1 < actions.Length do
                      yield actions.[i], actions.[i + 1]
                      i <- i + 2 ]
            let urgency =
                match hints with
                | null -> None
                | h ->
                    match h.TryGetValue "urgency" with
                    | true, v -> (try Some(Convert.ToByte v) with _ -> None)
                    | _ -> None
            let id =
                agg.Mutate(fun s ->
                    let store', id =
                        NotificationStore.add
                            (now ()) defaultTimeoutMs replacesId appName appIcon summary body acts urgency expireTimeout
                            s.Notifications
                    { s with Notifications = store' }, id)
            eprintfn "WTF notif: %s %s" appName summary
            Task.FromResult id

        member _.CloseNotificationAsync(id) =
            let present =
                agg.Mutate(fun s ->
                    let store', present = NotificationStore.close id s.Notifications
                    { s with Notifications = store' }, present)
            // reason 3 = closed by a CloseNotification call.
            if present then raiseClosed id 3u
            Task.CompletedTask

        // We store + emit + expose the live list; on-screen render is deferred.
        member _.GetCapabilitiesAsync() = Task.FromResult [| "body"; "actions"; "persistence" |]

        member _.GetServerInformationAsync() = Task.FromResult(struct ("WTF", "WTF", "0.1", "1.2"))

        [<CLIEvent>]
        member _.NotificationClosed = closedEvt.Publish

        [<CLIEvent>]
        member _.ActionInvoked = invokedEvt.Publish
