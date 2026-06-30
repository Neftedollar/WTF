namespace WTF.Client

open System
open System.Globalization
open System.Text.Json.Nodes

/// PURE (ImageSharp-free, display-free) content model for the status bar. It
/// turns the WM snapshot JSON (the SAME shape as WTF.Core.Protocol.snapshot, plus
/// the additive "desktop" object from WTF.Desktop.DesktopJson) plus a passed-in
/// clock time into an ordered list of bar segments the renderer draws. The clock
/// is an argument so the build is deterministic + unit-testable. This is the
/// verifiable heart of the bar — it must never throw on malformed/empty JSON.
module BarModel =

    type Segment =
        | Workspace of tag: string * current: bool * occupied: bool
        | Clock of string
        | Battery of percent: int * state: string
        | Network of string
        | Player of status: string * title: string * artist: string
        | Text of string

    type BarModel = { Left: Segment list; Right: Segment list }

    // NOTE: JsonNode's string indexer THROWS InvalidOperationException when the
    // node is not a JsonObject (e.g. an array element that is a bare number, or a
    // sub-value that arrived as a string/array). These getters therefore type-test
    // for JsonObject FIRST so one wrong-typed sub-value degrades to None instead of
    // unwinding and collapsing the whole bar — "the bar shows what it can".
    let private getStr (n: JsonNode) (key: string) : string option =
        match n with
        | :? JsonObject as o ->
            match o.[key] with
            | null -> None
            | v -> (try Some(v.GetValue<string>()) with _ -> None)
        | _ -> None

    let private getInt (n: JsonNode) (key: string) : int option =
        match n with
        | :? JsonObject as o ->
            match o.[key] with
            | null -> None
            | v -> (try Some(v.GetValue<int>()) with _ -> (try Some(int (v.GetValue<float>())) with _ -> None))
        | _ -> None

    let private getBool (n: JsonNode) (key: string) : bool =
        match n with
        | :? JsonObject as o ->
            match o.[key] with
            | null -> false
            | v -> (try v.GetValue<bool>() with _ -> false)
        | _ -> false

    /// Build the bar content model. `now` drives the clock (HH:mm). On ANY parse
    /// failure or missing "desktop", degrade to just the clock on the right — the
    /// bar shows what it can and never crashes.
    let build (now: DateTime) (snapshotJson: string) : BarModel =
        // InvariantCulture: in a custom format string ':' is the LOCALE time
        // separator placeholder, so fi-FI etc. would render "14.05" without it.
        let clock = Clock(now.ToString("HH:mm", CultureInfo.InvariantCulture))
        let fallback = { Left = []; Right = [ clock ] }
        match (try Some(JsonNode.Parse snapshotJson) with _ -> None) with
        | None -> fallback
        | Some root ->
            try
                let current = defaultArg (getStr root "current") ""

                // Left: a workspace pill per workspace, in order. current = tag is
                // focused; occupied = it has windows.
                let left =
                    match root.["workspaces"] with
                    | :? JsonArray as arr ->
                        [ for ws in arr do
                            match getStr ws "tag" with
                            | Some tag ->
                                let occupied =
                                    match ws.["windows"] with
                                    | :? JsonArray as wins -> wins.Count > 0
                                    | _ -> false
                                yield Workspace(tag, (tag = current), occupied)
                            | None -> () ]
                    | _ -> []

                // Right: now-playing (first Playing player), network, battery, clock.
                // `child` indexes only JsonObjects so a missing/wrong-typed "desktop"
                // (or container) yields null instead of throwing and collapsing the bar.
                let child (n: JsonNode) (key: string) : JsonNode =
                    match n with
                    | :? JsonObject as o -> o.[key]
                    | _ -> null
                let desktop = root.["desktop"]

                let player =
                    match child desktop "players" with
                    | :? JsonArray as players ->
                        players
                        |> Seq.tryPick (fun p ->
                            match getStr p "status" with
                            | Some s when s.Equals("Playing", StringComparison.OrdinalIgnoreCase) ->
                                Some(
                                    Player(
                                        s,
                                        defaultArg (getStr p "title") "",
                                        defaultArg (getStr p "artist") ""
                                    )
                                )
                            | _ -> None)
                    | _ -> None

                let network =
                    match child desktop "network" with
                    | null -> None
                    | net ->
                        let primary = getStr net "primary"
                        let state = getStr net "state"
                        match primary, state with
                        | Some p, _ when p <> "" -> Some(Network p)
                        | _, Some s -> Some(Network s)
                        | _ -> None

                let battery =
                    match child desktop "battery" with
                    | null -> None
                    | bat ->
                        match getInt bat "percent" with
                        // clamp to a sane 0..100 so a bogus snapshot can't make the
                        // renderer draw a nonsensical bar.
                        | Some pct -> Some(Battery(max 0 (min 100 pct), defaultArg (getStr bat "state") ""))
                        | None -> None

                let right =
                    [ match player with Some p -> yield p | None -> ()
                      match network with Some n -> yield n | None -> ()
                      match battery with Some b -> yield b | None -> ()
                      yield clock ]

                { Left = left; Right = right }
            with _ -> fallback
