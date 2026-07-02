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

    /// Resolve ONE configured segment spec against the snapshot. A data source
    /// that is absent (no battery, nothing playing) yields NO segments — the bar
    /// shows what it can. Workspaces expands to one pill segment per workspace.
    let private resolveSpec (now: DateTime) (root: JsonNode) (spec: ClientConfig.SegmentSpec) : Segment list =
        let child (n: JsonNode) (key: string) : JsonNode =
            match n with
            | :? JsonObject as o -> o.[key]
            | _ -> null
        let desktop = if isNull root then null else child root "desktop"
        match spec with
        | ClientConfig.SClock fmt ->
            // InvariantCulture: in a custom format string ':' is the LOCALE time
            // separator placeholder, so fi-FI etc. would render "14.05" without it.
            // A garbage format string degrades to HH:mm instead of throwing.
            let text =
                try now.ToString(fmt, CultureInfo.InvariantCulture)
                with _ -> now.ToString("HH:mm", CultureInfo.InvariantCulture)
            [ Clock text ]
        | ClientConfig.SLabel text -> [ Text text ]
        | ClientConfig.SWorkspaces ->
            if isNull root then []
            else
                let current = defaultArg (getStr root "current") ""
                match child root "workspaces" with
                | :? JsonArray as arr ->
                    [ for ws in arr do
                        match getStr ws "tag" with
                        | Some tag ->
                            let occupied =
                                match child ws "windows" with
                                | :? JsonArray as wins -> wins.Count > 0
                                | _ -> false
                            yield Workspace(tag, (tag = current), occupied)
                        | None -> () ]
                | _ -> []
        | ClientConfig.SPlayer ->
            match child desktop "players" with
            | :? JsonArray as players ->
                players
                |> Seq.tryPick (fun p ->
                    match getStr p "status" with
                    | Some st when st.Equals("Playing", StringComparison.OrdinalIgnoreCase) ->
                        Some(Player(st, defaultArg (getStr p "title") "", defaultArg (getStr p "artist") ""))
                    | _ -> None)
                |> Option.toList
            | _ -> []
        | ClientConfig.SNetwork ->
            match child desktop "network" with
            | null -> []
            | net ->
                match getStr net "primary", getStr net "state" with
                | Some p, _ when p <> "" -> [ Network p ]
                | _, Some st -> [ Network st ]
                | _ -> []
        | ClientConfig.SBattery ->
            match child desktop "battery" with
            | null -> []
            | bat ->
                match getInt bat "percent" with
                // clamp to a sane 0..100 so a bogus snapshot can't make the
                // renderer draw a nonsensical bar.
                | Some pct -> [ Battery(max 0 (min 100 pct), defaultArg (getStr bat "state") "") ]
                | None -> []

    /// Build the bar content model from CONFIGURED segment lists. Total: any
    /// parse failure degrades to whatever specs can render without a snapshot
    /// (clock/labels) — the bar shows what it can and never crashes.
    let buildWith (leftSpec: ClientConfig.SegmentSpec list) (rightSpec: ClientConfig.SegmentSpec list)
                  (now: DateTime) (snapshotJson: string) : BarModel =
        let root =
            match (try Some(JsonNode.Parse snapshotJson) with _ -> None) with
            | Some r -> r
            | None -> null
        let resolveAll specs =
            specs |> List.collect (fun spec -> try resolveSpec now root spec with _ -> [])
        { Left = resolveAll leftSpec; Right = resolveAll rightSpec }

    /// The pre-config composition (workspaces left; player/network/battery/clock
    /// right). Kept as the compatibility entry point — used by tests and by any
    /// caller that has no ui config.
    let build (now: DateTime) (snapshotJson: string) : BarModel =
        buildWith ClientConfig.barDefaults.Left ClientConfig.barDefaults.Right now snapshotJson
