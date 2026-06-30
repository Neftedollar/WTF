namespace WTF.Desktop

open System
open System.Diagnostics
open System.Collections.Concurrent
open System.Collections.Generic
open System.Threading.Tasks
open Tmds.DBus
open WTF.Desktop.Models
open WTF.Desktop.DBus

/// Best-effort volume/mute control. Media VOLUME keys are NOT MPRIS — they go to
/// PipeWire/ALSA. We shell out to `wpctl` (PipeWire) first, then `pactl`
/// (PulseAudio); if neither exists the keypress is a documented no-op. Spawns are
/// fire-and-forget (never waited on), so the loop thread never stalls.
module Volume =

    let private spawn (file: string) (args: string) : bool =
        try
            let psi = ProcessStartInfo(file, args)
            psi.UseShellExecute <- false
            // Don't redirect the child's pipes: a volume tool's output is trivial,
            // and capturing pipes we never drain would leak handles (and risk a
            // pipe-full hang) over a long session. Dispose the handle promptly —
            // Dispose releases our handle without killing the (fire-and-forget) child.
            use p = Process.Start psi
            ignore p
            true
        with _ ->
            false

    let private run (wpctlArgs: string) (pactlArgs: string) =
        if not (spawn "wpctl" wpctlArgs) then
            spawn "pactl" pactlArgs |> ignore

    let toggleMute () =
        run "set-mute @DEFAULT_AUDIO_SINK@ toggle" "set-sink-mute @DEFAULT_SINK@ toggle"

    /// Build the (wpctl, pactl) argument strings for a volume delta. Pure +
    /// testable. The magnitude is computed in int64 so `abs` never overflows
    /// (abs Int32.MinValue would throw OverflowException). `pct` may be negative
    /// (lower) or positive (raise).
    let formatArgs (pct: int) : string * string =
        let mag = abs (int64 pct)
        let wp =
            if pct >= 0 then sprintf "set-volume @DEFAULT_AUDIO_SINK@ %d%%+" mag
            else sprintf "set-volume @DEFAULT_AUDIO_SINK@ %d%%-" mag
        let pa =
            if pct >= 0 then sprintf "set-sink-volume @DEFAULT_SINK@ +%d%%" mag
            else sprintf "set-sink-volume @DEFAULT_SINK@ -%d%%" mag
        wp, pa

    /// `pct` may be negative (lower) or positive (raise).
    let adjust (pct: int) =
        let wp, pa = formatArgs pct
        run wp pa


/// The D-Bus CLIENT proxies feeding the pure desktop-state records. Each starter
/// is fully wrapped: a missing service / absent device / failed read logs and
/// degrades quietly — one client failing never affects the others or the WM.
module Clients =

    let private mprisPrefix = "org.mpris.MediaPlayer2."

    // -- UPower (battery) ------------------------------------------------------
    let startUPower (conn: Connection) (agg: Aggregator) : Task =
        task {
            try
                let dev =
                    conn.CreateProxy<IUPowerDevice>(
                        "org.freedesktop.UPower",
                        ObjectPath "/org/freedesktop/UPower/devices/DisplayDevice")

                let apply (p: BatteryProps) =
                    let bs =
                        { Present = p.IsPresent
                          Percentage = p.Percentage
                          State = upowerStateLabel p.State }
                    agg.Update(fun s -> { s with Battery = Some bs })

                let! props = dev.GetAllAsync()
                apply props

                let! _ =
                    dev.WatchPropertiesAsync(
                        Action<PropertyChanges>(fun _ ->
                            (task {
                                try
                                    let! p = dev.GetAllAsync()
                                    apply p
                                with _ -> ()
                             }) |> ignore))

                eprintfn "WTF desktop: UPower battery present=%b pct=%.0f" props.IsPresent props.Percentage
            with ex ->
                // No DisplayDevice (e.g. a desktop without a battery) or no service.
                eprintfn "WTF desktop: UPower unavailable (%s)" ex.Message
        }
        :> Task

    // -- NetworkManager --------------------------------------------------------
    let startNetwork (conn: Connection) (agg: Aggregator) : Task =
        task {
            try
                let nm =
                    conn.CreateProxy<INetworkManager>(
                        "org.freedesktop.NetworkManager",
                        ObjectPath "/org/freedesktop/NetworkManager")

                let apply (p: NetworkProps) =
                    let primary =
                        let path = p.PrimaryConnection.ToString()
                        if String.IsNullOrEmpty path || path = "/" then None else Some path
                    let ns =
                        { State = nmStateLabel p.State
                          Connectivity = nmConnectivityLabel p.Connectivity
                          Primary = primary }
                    agg.Update(fun s -> { s with Network = Some ns })

                let! props = nm.GetAllAsync()
                apply props

                let! _ =
                    nm.WatchPropertiesAsync(
                        Action<PropertyChanges>(fun _ ->
                            (task {
                                try
                                    let! p = nm.GetAllAsync()
                                    apply p
                                with _ -> ()
                             }) |> ignore))

                eprintfn "WTF desktop: NetworkManager state=%s" (nmStateLabel props.State)
            with ex ->
                eprintfn "WTF desktop: NetworkManager unavailable (%s)" ex.Message
        }
        :> Task

    // -- logind (suspend / lock observation) -----------------------------------
    let startLogind (conn: Connection) (agg: Aggregator) : Task =
        task {
            try
                let mgr =
                    conn.CreateProxy<ILogindManager>(
                        "org.freedesktop.login1",
                        ObjectPath "/org/freedesktop/login1")

                let! _ =
                    mgr.WatchPrepareForSleepAsync(
                        Action<bool>(fun sleeping ->
                            agg.Update(fun s ->
                                { s with Power = { s.Power with PreparingForSleep = sleeping } })
                            eprintfn "WTF desktop: PrepareForSleep %b" sleeping))
                // HOOK: on `sleeping=true`, pre-sleep work (and releasing a delay
                // inhibitor) belongs here. A future lock-on-suspend that touches
                // wlroots/World MUST be routed through bridge.Submit(wtf_command_
                // notify, ...) like Ipc — never a direct call. Visual lock deferred.

                // Subscribe to the current session's Lock / Unlock signals.
                match Environment.GetEnvironmentVariable "XDG_SESSION_ID" with
                | null | "" -> eprintfn "WTF desktop: no XDG_SESSION_ID — session lock signals skipped"
                | sid ->
                    try
                        let! sessionPath = mgr.GetSessionAsync sid
                        let sess = conn.CreateProxy<ILogindSession>("org.freedesktop.login1", sessionPath)
                        let! _ =
                            sess.WatchLockAsync(
                                Action(fun () ->
                                    agg.Update(fun s -> { s with Power = { s.Power with SessionLocked = true } })
                                    eprintfn "WTF desktop: session Lock"))
                        let! _ =
                            sess.WatchUnlockAsync(
                                Action(fun () ->
                                    agg.Update(fun s -> { s with Power = { s.Power with SessionLocked = false } })
                                    eprintfn "WTF desktop: session Unlock"))
                        ()
                    with ex ->
                        eprintfn "WTF desktop: logind session signals unavailable (%s)" ex.Message

                eprintfn "WTF desktop: logind connected"
            with ex ->
                eprintfn "WTF desktop: logind unavailable (%s)" ex.Message
        }
        :> Task

    // -- MPRIS (media players) -------------------------------------------------
    /// Wires up MPRIS enumeration + live tracking. `registry` maps a player bus
    /// name to its proxy so `Desktop.sendMedia` can drive the active one.
    let startMpris
        (conn: Connection)
        (agg: Aggregator)
        (registry: ConcurrentDictionary<string, IMprisPlayer>)
        : Task =
        task {
            try
                let untrack (bus: string) =
                    registry.TryRemove bus |> ignore
                    agg.Update(fun s -> { s with Players = s.Players |> List.filter (fun p -> p.Bus <> bus) })

                let track (bus: string) : Task =
                    task {
                        try
                            let player = conn.CreateProxy<IMprisPlayer>(bus, ObjectPath "/org/mpris/MediaPlayer2")
                            let root = conn.CreateProxy<IMprisRoot>(bus, ObjectPath "/org/mpris/MediaPlayer2")
                            registry.[bus] <- player

                            let! identity =
                                task {
                                    try
                                        let! v = root.GetAsync "Identity"
                                        return (if isNull v then bus.Substring mprisPrefix.Length else string v)
                                    with _ ->
                                        return bus.Substring mprisPrefix.Length
                                }

                            let apply (props: IDictionary<string, obj>) =
                                let getS k =
                                    match props.TryGetValue k with
                                    | true, v when not (isNull v) -> string v
                                    | _ -> ""
                                let status = getS "PlaybackStatus"
                                let canControl =
                                    match props.TryGetValue "CanControl" with
                                    | true, v -> (try Convert.ToBoolean v with _ -> false)
                                    | _ -> false
                                let title, artist =
                                    match props.TryGetValue "Metadata" with
                                    | true, (:? IDictionary<string, obj> as md) ->
                                        let t =
                                            match md.TryGetValue "xesam:title" with
                                            | true, v when not (isNull v) -> string v
                                            | _ -> ""
                                        let a =
                                            match md.TryGetValue "xesam:artist" with
                                            | true, (:? (string[]) as arr) -> String.Join(", ", arr)
                                            | true, v when not (isNull v) -> string v
                                            | _ -> ""
                                        t, a
                                    | _ -> "", ""
                                let mp =
                                    { Bus = bus
                                      Identity = identity
                                      Status = status
                                      Title = title
                                      Artist = artist
                                      CanControl = canControl }
                                agg.Update(fun s ->
                                    { s with Players = (s.Players |> List.filter (fun p -> p.Bus <> bus)) @ [ mp ] })

                            let! props = player.GetAllAsync()
                            apply props

                            let! _ =
                                player.WatchPropertiesAsync(
                                    Action<PropertyChanges>(fun _ ->
                                        (task {
                                            try
                                                let! p = player.GetAllAsync()
                                                apply p
                                            with _ -> ()
                                         }) |> ignore))

                            eprintfn "WTF desktop: MPRIS player %s (%s)" identity bus
                        with ex ->
                            eprintfn "WTF desktop: MPRIS track %s failed (%s)" bus ex.Message
                    }
                    :> Task

                // Enumerate players already on the bus.
                let! names = conn.ListServicesAsync()
                for n in names do
                    if n.StartsWith mprisPrefix then
                        do! track n

                // Track players appearing / disappearing (their bus names are
                // dynamic, so watch the whole NameOwnerChanged firehose + filter).
                let busd = conn.CreateProxy<IBusDaemon>("org.freedesktop.DBus", ObjectPath "/org/freedesktop/DBus")
                let! _ =
                    busd.WatchNameOwnerChangedAsync(
                        Action<struct (string * string * string)>(fun args ->
                            let struct (name, oldOwner, newOwner) = args
                            if name.StartsWith mprisPrefix then
                                // newOwner<>"" covers BOTH a fresh appearance (oldOwner="")
                                // AND an owner transfer (oldOwner<>"" && newOwner<>""): in
                                // either case re-track so a handed-off bus name isn't left
                                // with a stale proxy. newOwner="" means the player vanished.
                                if newOwner <> "" then track name |> ignore
                                else untrack name))

                eprintfn "WTF desktop: MPRIS watching for players"
            with ex ->
                eprintfn "WTF desktop: MPRIS unavailable (%s)" ex.Message
        }
        :> Task
