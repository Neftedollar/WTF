namespace WTF.Desktop

open System
open System.Threading.Tasks
open System.Collections.Generic
open Tmds.DBus

/// The Tmds.DBus interface declarations (server-side notification interface +
/// client proxy interfaces). Tmds reflects over the `[<DBusInterface>]`-annotated
/// types: a method `FooAsync` maps to the wire method `Foo`; `WatchFooAsync`
/// subscribes to signal `Foo`; a `[<CLIEvent>]` member emits a signal when
/// triggered. Multi-value/struct args MUST be System.ValueTuple (`struct(...)`)
/// to marshal — a plain F# tuple is System.Tuple and will NOT.
module DBus =

    // -- org.freedesktop.Notifications (SERVER: we own & implement this) --------
    [<DBusInterface("org.freedesktop.Notifications")>]
    type IFreedesktopNotifications =
        abstract member NotifyAsync:
            appName: string *
            replacesId: uint32 *
            appIcon: string *
            summary: string *
            body: string *
            actions: string[] *
            hints: IDictionary<string, obj> *
            expireTimeout: int ->
                Task<uint32>

        abstract member CloseNotificationAsync: id: uint32 -> Task
        abstract member GetCapabilitiesAsync: unit -> Task<string[]>
        abstract member GetServerInformationAsync: unit -> Task<struct (string * string * string * string)>

        [<CLIEvent>]
        abstract member NotificationClosed: IDelegateEvent<Action<struct (uint32 * uint32)>>

        [<CLIEvent>]
        abstract member ActionInvoked: IDelegateEvent<Action<struct (uint32 * string)>>

    // -- org.freedesktop.DBus (bus daemon: watch arbitrary name owner changes) --
    [<DBusInterface("org.freedesktop.DBus")>]
    type IBusDaemon =
        /// NameOwnerChanged(name, oldOwner, newOwner) — used to track MPRIS players
        /// appearing/disappearing (their bus names are dynamic, so a single
        /// ResolveServiceOwner is not enough; we filter this firehose by prefix).
        abstract member WatchNameOwnerChangedAsync:
            handler: Action<struct (string * string * string)> -> Task<IDisposable>

    // -- org.freedesktop.login1 (CLIENT) ---------------------------------------
    [<DBusInterface("org.freedesktop.login1.Manager")>]
    type ILogindManager =
        abstract member GetSessionAsync: sessionId: string -> Task<ObjectPath>
        abstract member WatchPrepareForSleepAsync: handler: Action<bool> -> Task<IDisposable>
        /// Delay-inhibitor fd (released to let sleep proceed). Optional hook.
        abstract member InhibitAsync: what: string * who: string * why: string * mode: string -> Task<CloseSafeHandle>

    [<DBusInterface("org.freedesktop.login1.Session")>]
    type ILogindSession =
        abstract member WatchLockAsync: handler: Action -> Task<IDisposable>
        abstract member WatchUnlockAsync: handler: Action -> Task<IDisposable>

    // -- org.freedesktop.UPower.Device (CLIENT) --------------------------------
    [<Dictionary>]
    type BatteryProps() =
        member val IsPresent = false with get, set
        member val Percentage = 0.0 with get, set
        member val State = 0u with get, set

    [<DBusInterface("org.freedesktop.UPower.Device")>]
    type IUPowerDevice =
        abstract member GetAllAsync: unit -> Task<BatteryProps>
        abstract member WatchPropertiesAsync: handler: Action<PropertyChanges> -> Task<IDisposable>

    // -- org.freedesktop.NetworkManager (CLIENT) -------------------------------
    [<Dictionary>]
    type NetworkProps() =
        member val State = 0u with get, set
        member val Connectivity = 0u with get, set
        member val PrimaryConnection: ObjectPath = ObjectPath("/") with get, set

    [<DBusInterface("org.freedesktop.NetworkManager")>]
    type INetworkManager =
        abstract member GetAllAsync: unit -> Task<NetworkProps>
        abstract member WatchPropertiesAsync: handler: Action<PropertyChanges> -> Task<IDisposable>

    // -- org.mpris.MediaPlayer2.* (CLIENT) -------------------------------------
    [<DBusInterface("org.mpris.MediaPlayer2")>]
    type IMprisRoot =
        /// Property getter (org.freedesktop.DBus.Properties.Get for this iface).
        abstract member GetAsync: prop: string -> Task<obj>

    [<DBusInterface("org.mpris.MediaPlayer2.Player")>]
    type IMprisPlayer =
        abstract member PlayPauseAsync: unit -> Task
        abstract member PlayAsync: unit -> Task
        abstract member PauseAsync: unit -> Task
        abstract member NextAsync: unit -> Task
        abstract member PreviousAsync: unit -> Task
        abstract member StopAsync: unit -> Task
        abstract member GetAllAsync: unit -> Task<IDictionary<string, obj>>
        abstract member WatchPropertiesAsync: handler: Action<PropertyChanges> -> Task<IDisposable>
