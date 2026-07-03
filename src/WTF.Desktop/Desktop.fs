namespace WTF.Desktop

open System
open System.Collections.Concurrent
open System.Threading.Tasks
open System.Text.Json.Nodes
open Tmds.DBus
open WTF.Desktop.Models
open WTF.Desktop.DBus

/// The single public entry point for the native D-Bus desktop shell.
///
/// THREADING: `start` is called FIRE-AND-FORGET from the Host's `onReady` (on the
/// WM loop thread). It kicks off a `task` that connects + wires every service on
/// Tmds background threads and RETURNS IMMEDIATELY — `onReady` never awaits it and
/// returns promptly regardless of bus availability. Every D-Bus callback runs on a
/// Tmds I/O thread and only reads/stores via the lock-guarded `Aggregator` or
/// calls OUT to D-Bus; none touch wlroots/World, so NO LoopBridge is needed. If a
/// future flow must touch the WM (e.g. lock-on-suspend), route it through
/// `bridge.Submit(Ffi.wtf_command_notify, line)` exactly like Ipc — never directly.
///
/// GRACEFUL DEGRADATION: no session bus, name already owned, or any service
/// failing to init is logged and skipped — it never blocks `onReady` or crashes
/// the WM.
module Desktop =

    // Long-lived roots: keep the connection, the exported notification object (so
    // the GC can't collect it / its signal delegates while the WM runs), the
    // aggregator, and the MPRIS proxy registry alive for the whole process.
    let private agg = Aggregator()
    let private mprisRegistry = ConcurrentDictionary<string, IMprisPlayer>()
    let mutable private conn: Connection = null
    // UPower / NetworkManager / logind live on the SYSTEM bus, not the session
    // bus — kept alive alongside `conn` for the whole process.
    let mutable private sysConn: Connection = null
    let mutable private daemon: NotificationDaemon option = None

    /// The live desktop-shell state for the agent (thread-safe read).
    let snapshot () : DesktopState = agg.Snapshot()

    /// Render the live state as the `extra` JsonObject the Host splices under the
    /// snapshot's "desktop" key via `Protocol.snapshotLineWith`.
    let snapshotJson () : JsonObject = DesktopJson.render (agg.Snapshot())

    /// Inject a notification straight into the daemon's OWN store via the
    /// Aggregator lock — the identical mutation `NotificationDaemon.NotifyAsync`
    /// performs. Because it reuses the same Aggregator/NotificationStore the daemon
    /// owns and the snapshot's "desktop" object reads, the notification immediately
    /// surfaces in our store + in the agent snapshot (and on a bar in Phase 4); the
    /// daemon's ~1s expiry Timer reaps it normally. Thread-safe (Aggregator gate),
    /// so it needs NO LoopBridge — the agent `notify` tool and the socket
    /// {"notify":{summary,body}} verb both drive it. Works even when another daemon
    /// owns the bus name (we still record it in our own store/snapshot).
    let notify (summary: string) (body: string) : unit =
        let nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        agg.Mutate(fun s ->
            let store', _ =
                NotificationStore.add nowMs 5000 0u "wtf-agent" "" summary body [] None -1 s.Notifications
            { s with Notifications = store' }, ())
        eprintfn "WTF notif: wtf-agent %s" summary

    // Own org.freedesktop.Notifications (best-effort). If another daemon already
    // holds the name, RegisterServiceAsync throws — we log and continue with the
    // client features (battery/network/media still work).
    let private startNotifications (c: Connection) : Task =
        task {
            try
                let d = NotificationDaemon(agg)
                do! c.RegisterObjectAsync d

                try
                    do! c.RegisterServiceAsync("org.freedesktop.Notifications", ServiceRegistrationOptions.None)
                    d.Start()
                    daemon <- Some d
                    eprintfn "WTF desktop: owning org.freedesktop.Notifications"
                with ex ->
                    eprintfn "WTF desktop: another daemon present, notifications disabled (%s)" ex.Message
            with ex ->
                eprintfn "WTF desktop: notification daemon init failed (%s)" ex.Message
        }
        :> Task

    /// Connect to the session bus and wire up every service. FIRE-AND-FORGET:
    /// returns immediately; all work happens on Tmds background threads.
    let start () : unit =
        let run =
            task {
                try
                    match Address.Session with
                    | null -> eprintfn "WTF desktop: no DBUS_SESSION_BUS_ADDRESS — desktop shell disabled"
                    | addr ->
                        let c = new Connection(addr)
                        let! _ = c.ConnectAsync()
                        conn <- c
                        // Session bus: notifications + MPRIS media live here.
                        do! startNotifications c
                        do! Clients.startMpris c agg mprisRegistry
                        // System bus: UPower / NetworkManager / logind live here,
                        // NOT the session bus — connecting them to `c` was why
                        // battery/network/lock reported ServiceUnknown. Separate,
                        // best-effort connection so a locked-down system bus still
                        // leaves notifications/media working.
                        try
                            match Address.System with
                            | null ->
                                eprintfn "WTF desktop: no system bus address — battery/network/lock disabled"
                            | saddr ->
                                let sc = new Connection(saddr)
                                let! _ = sc.ConnectAsync()
                                sysConn <- sc
                                do! Clients.startUPower sc agg
                                do! Clients.startNetwork sc agg
                                do! Clients.startLogind sc agg
                        with ex ->
                            eprintfn "WTF desktop: system bus unavailable, battery/network/lock disabled (%s)" ex.Message
                        eprintfn "WTF desktop: D-Bus shell up"
                with ex ->
                    eprintfn "WTF desktop: init failed, continuing without shell (%s)" ex.Message
            }

        run |> ignore // fire-and-forget; do not await on the loop thread.

    /// Drive a media key. Transport actions (play/pause/next/prev/stop) go to the
    /// active MPRIS player (WM->DBus, fire-and-forget so `onKey` never stalls).
    /// Volume/mute are NOT MPRIS — best-effort shell to wpctl/pactl. The active
    /// player = first reporting "Playing", else the first known.
    let sendMedia (action: MediaAction) : unit =
        match action with
        | Mute -> Volume.toggleMute ()
        | VolUp -> Volume.adjust 5
        | VolDown -> Volume.adjust -5
        | _ ->
            let job =
                task {
                    try
                        let players = agg.Snapshot().Players
                        let pick =
                            players
                            |> List.tryFind (fun p -> p.Status = "Playing")
                            |> Option.orElseWith (fun () -> List.tryHead players)
                        match pick |> Option.bind (fun mp ->
                                  match mprisRegistry.TryGetValue mp.Bus with
                                  | true, pl -> Some pl
                                  | _ -> None) with
                        | Some player ->
                            do!
                                match action with
                                | Play -> player.PlayAsync()
                                | Pause -> player.PauseAsync()
                                | PlayPause -> player.PlayPauseAsync()
                                | Stop -> player.StopAsync()
                                | Next -> player.NextAsync()
                                | Prev -> player.PreviousAsync()
                                | _ -> Task.CompletedTask
                        | None -> ()
                    with _ -> ()
                }

            job |> ignore
