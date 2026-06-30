namespace WTF.Desktop

/// PURE, dependency-free desktop-shell model layer (no Tmds.DBus, no I/O, no
/// clock side-effects: `now` is always passed in). The Tmds.DBus plumbing in
/// the sibling modules drives these records; everything here is unit-testable.
module Models =

    // ---------------------------------------------------------------------
    // Notification store (org.freedesktop.Notifications, in-memory + expiry)
    // ---------------------------------------------------------------------

    /// One live notification. `Actions` is a list of (key, label) pairs as the
    /// Notify spec delivers them. `ExpiresAtMs = None` means "never expires".
    type Notification =
        { Id: uint32
          ReplacesId: uint32
          AppName: string
          AppIcon: string
          Summary: string
          Body: string
          Actions: (string * string) list
          Urgency: byte option
          CreatedMs: int64
          ExpiresAtMs: int64 option }

    /// The live set of notifications plus the next id to hand out. `Active` is
    /// kept newest-first; a replace keeps the replaced item's original position.
    type NotificationStore =
        { Active: Notification list
          NextId: uint32 }

    [<RequireQualifiedAccess>]
    module NotificationStore =

        /// Empty store. Ids start at 1 (0 is reserved / "no id" by the spec).
        let empty = { Active = []; NextId = 1u }

        /// Advance an id, skipping 0 on wrap-around (0 is never a valid id).
        let private bump (id: uint32) =
            let n = id + 1u
            if n = 0u then 1u else n

        /// Resolve the absolute expiry from the Notify timeout (ms):
        ///   -1 -> server default (`None` if the default is <= 0)
        ///    0 -> never expire (`None`)
        ///   >0 -> `Some (now + timeout)`
        let private resolveExpiry (now: int64) (defaultTimeoutMs: int) (timeout: int) =
            match timeout with
            | -1 -> if defaultTimeoutMs <= 0 then None else Some(now + int64 defaultTimeoutMs)
            | 0 -> None
            | t when t > 0 -> Some(now + int64 t)
            | _ -> None

        /// Add (or replace) a notification, returning the new store and the id
        /// the caller should report back to the bus.
        ///   * `replaces <> 0` and present -> reuse that id, overwrite content
        ///     in place (keeping list position).
        ///   * otherwise -> allocate `NextId`, then bump it (skipping 0 on wrap).
        let add
            (now: int64)
            (defaultTimeoutMs: int)
            (replaces: uint32)
            (appName: string)
            (appIcon: string)
            (summary: string)
            (body: string)
            (actions: (string * string) list)
            (urgency: byte option)
            (timeout: int)
            (store: NotificationStore)
            : NotificationStore * uint32 =
            let expiresAt = resolveExpiry now defaultTimeoutMs timeout
            let mk id =
                { Id = id
                  ReplacesId = replaces
                  AppName = appName
                  AppIcon = appIcon
                  Summary = summary
                  Body = body
                  Actions = actions
                  Urgency = urgency
                  CreatedMs = now
                  ExpiresAtMs = expiresAt }
            let replacing =
                replaces <> 0u && store.Active |> List.exists (fun n -> n.Id = replaces)
            if replacing then
                let active =
                    store.Active
                    |> List.map (fun n -> if n.Id = replaces then mk replaces else n)
                { store with Active = active }, replaces
            else
                let id = store.NextId
                { Active = mk id :: store.Active; NextId = bump id }, id

        /// Close a notification by id. The bool is "was it present" — closing an
        /// absent id is a no-op (`false`) so the caller can gate NotificationClosed.
        let close (id: uint32) (store: NotificationStore) : NotificationStore * bool =
            if store.Active |> List.exists (fun n -> n.Id = id) then
                { store with Active = store.Active |> List.filter (fun n -> n.Id <> id) }, true
            else
                store, false

        /// Drop everything whose expiry is <= `now`. Returns the removed ids in
        /// ascending-expiry order (earliest first) so the caller emits
        /// NotificationClosed(reason = 1 = expired) in the right sequence.
        let expire (now: int64) (store: NotificationStore) : NotificationStore * uint32 list =
            let isExpired (n: Notification) =
                match n.ExpiresAtMs with
                | Some t -> t <= now
                | None -> false
            let removedIds =
                store.Active
                |> List.filter isExpired
                |> List.sortBy (fun n -> match n.ExpiresAtMs with Some t -> t | None -> System.Int64.MaxValue)
                |> List.map (fun n -> n.Id)
            let remaining = store.Active |> List.filter (isExpired >> not)
            { store with Active = remaining }, removedIds

        /// Look up a live notification by id.
        let tryFind (id: uint32) (store: NotificationStore) =
            store.Active |> List.tryFind (fun n -> n.Id = id)

        /// The live notifications (newest-first).
        let toList (store: NotificationStore) = store.Active

    // ---------------------------------------------------------------------
    // Desktop state records (battery / power / network / media players)
    // ---------------------------------------------------------------------

    /// UPower battery summary. `Percentage` is 0..100.
    type BatteryState =
        { Present: bool
          Percentage: float
          State: string } // charging/discharging/full/empty/unknown

    /// logind-derived power/session state.
    type PowerState =
        { PreparingForSleep: bool
          SessionLocked: bool }

    /// NetworkManager connectivity summary.
    type NetworkState =
        { State: string
          Connectivity: string
          Primary: string option }

    /// One MPRIS media player.
    type MediaPlayer =
        { Bus: string
          Identity: string
          Status: string
          Title: string
          Artist: string
          CanControl: bool }

    /// The whole live desktop-shell state the aggregator holds and the agent reads.
    type DesktopState =
        { Notifications: NotificationStore
          Battery: BatteryState option
          Power: PowerState
          Network: NetworkState option
          Players: MediaPlayer list }

    [<RequireQualifiedAccess>]
    module PowerState =
        /// Nothing happening: awake and unlocked.
        let initial = { PreparingForSleep = false; SessionLocked = false }

    [<RequireQualifiedAccess>]
    module DesktopState =
        /// Empty state: no notifications, no devices observed yet.
        let empty =
            { Notifications = NotificationStore.empty
              Battery = None
              Power = PowerState.initial
              Network = None
              Players = [] }

    // ---------------------------------------------------------------------
    // Pure state-code mappers (D-Bus enum ints -> human labels)
    // ---------------------------------------------------------------------

    /// UPower Device.State -> label.
    let upowerStateLabel (u: uint32) : string =
        match u with
        | 1u -> "charging"
        | 2u -> "discharging"
        | 3u -> "empty"
        | 4u -> "fully-charged"
        | 5u -> "pending-charge"
        | 6u -> "pending-discharge"
        | _ -> "unknown"

    /// NetworkManager NMState -> label.
    let nmStateLabel (u: uint32) : string =
        match u with
        | 10u -> "asleep"
        | 20u -> "disconnected"
        | 30u -> "disconnecting"
        | 40u -> "connecting"
        | 50u -> "connected-local"
        | 60u -> "connected-site"
        | 70u -> "connected-global"
        | _ -> "unknown"

    /// NetworkManager NMConnectivityState -> label.
    let nmConnectivityLabel (u: uint32) : string =
        match u with
        | 1u -> "none"
        | 2u -> "portal"
        | 3u -> "limited"
        | 4u -> "full"
        | _ -> "unknown"

    // ---------------------------------------------------------------------
    // Media keys (pure keysym -> action mapping; wiring is in the Host)
    // ---------------------------------------------------------------------

    /// A transport/volume action a media key maps to.
    type MediaAction =
        | Play
        | Pause
        | PlayPause
        | Stop
        | Next
        | Prev
        | Mute
        | VolUp
        | VolDown

    [<RequireQualifiedAccess>]
    module MediaKey =

        // XKB XF86Audio* keysyms (confirmed against the brief).
        [<Literal>]
        let XF86AudioLowerVolume = 0x1008FF11u
        [<Literal>]
        let XF86AudioMute = 0x1008FF12u
        [<Literal>]
        let XF86AudioRaiseVolume = 0x1008FF13u
        [<Literal>]
        let XF86AudioPlay = 0x1008FF14u
        [<Literal>]
        let XF86AudioStop = 0x1008FF15u
        [<Literal>]
        let XF86AudioPrev = 0x1008FF16u
        [<Literal>]
        let XF86AudioNext = 0x1008FF17u
        [<Literal>]
        let XF86AudioPause = 0x1008FF31u

        /// Map an X keysym to a media action, or `None` if it is not a media key.
        let ofKeysym (keysym: uint32) : MediaAction option =
            match keysym with
            | XF86AudioLowerVolume -> Some VolDown
            | XF86AudioMute -> Some Mute
            | XF86AudioRaiseVolume -> Some VolUp
            | XF86AudioPlay -> Some PlayPause
            | XF86AudioStop -> Some Stop
            | XF86AudioPrev -> Some Prev
            | XF86AudioNext -> Some Next
            | XF86AudioPause -> Some Pause
            | _ -> None
