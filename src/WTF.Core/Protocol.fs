namespace WTF.Core

open System.Text.Json
open System.Text.Json.Nodes

/// The agent-facing wire format. Two directions:
///   * `snapshot`  : World  -> JSON the agent reads to understand the screen.
///   * `parse`     : JSON   -> Command the agent issues to act.
/// Deliberately flat and self-describing so an LLM can use it with no SDK.
module Protocol =

    let private rectJson (r: Rect) =
        let o = JsonObject()
        o["x"] <- JsonValue.Create(int r.X)
        o["y"] <- JsonValue.Create(int r.Y)
        o["w"] <- JsonValue.Create(int r.Width)
        o["h"] <- JsonValue.Create(int r.Height)
        o :> JsonNode

    /// A rich, semantic snapshot: workspaces, the windows in each (in order),
    /// what's focused, available layouts, and the computed geometry.
    let private buildSnapshot (extra: JsonObject option) (w: World) : JsonObject =
        let root = JsonObject()
        root["current"] <- JsonValue.Create w.Current
        root["nmaster"] <- JsonValue.Create w.Nmaster
        root["ratio"] <- JsonValue.Create w.Ratio
        root["gaps"] <- JsonValue.Create w.Gaps
        root["screen"] <- rectJson w.Screen

        let layouts = JsonArray()
        for n in Registry.names () do
            layouts.Add(JsonValue.Create n)
        root["layouts"] <- layouts

        let workspaces = JsonArray()
        for ws in w.Workspaces do
            let wo = JsonObject()
            wo["tag"] <- JsonValue.Create ws.Tag
            wo["layout"] <- JsonValue.Create ws.Layout
            let ids = JsonArray()
            let focusedId =
                match ws.Stack with
                | Some s ->
                    for id in Stack.toList s do
                        ids.Add(JsonValue.Create id)
                    Some s.Focus
                | None -> None
            wo["windows"] <- ids
            wo["focused"] <-
                match focusedId with
                | Some f -> JsonValue.Create f
                | None -> null
            // Additive: expose the floating members (with geometry) and the
            // fullscreen id so the agent sees the full picture.
            let floating = JsonArray()
            for KeyValue(id, r) in ws.Floating do
                let fo = rectJson r :?> JsonObject
                fo["id"] <- JsonValue.Create id
                floating.Add fo
            wo["floating"] <- floating
            wo["fullscreen"] <-
                match ws.Fullscreen with
                | Some id -> JsonValue.Create id
                | None -> null
            workspaces.Add wo
        root["workspaces"] <- workspaces

        let windows = JsonObject()
        for KeyValue(id, info) in w.Windows do
            let io = JsonObject()
            io["appId"] <- JsonValue.Create info.AppId
            io["title"] <- JsonValue.Create info.Title
            io["floating"] <- JsonValue.Create info.Floating
            windows[string id] <- io
        root["windows"] <- windows

        let arrange = JsonArray()
        for (id, r) in World.arrange w do
            let a = JsonObject()
            a["id"] <- JsonValue.Create id
            a["x"] <- JsonValue.Create(int r.X)
            a["y"] <- JsonValue.Create(int r.Y)
            a["w"] <- JsonValue.Create(int r.Width)
            a["h"] <- JsonValue.Create(int r.Height)
            arrange.Add a
        root["arrange"] <- arrange
        // Additive: a non-Core subsystem (the D-Bus desktop shell, WTF.Desktop)
        // can splice its own state under "desktop" without Core depending on it.
        match extra with
        | Some d -> root["desktop"] <- d
        | None -> ()
        root

    /// Pretty, multi-line snapshot (human / CLI).
    let snapshot (w: World) : string =
        (buildSnapshot None w).ToJsonString(JsonSerializerOptions(WriteIndented = true))

    /// Compact single-line snapshot (NDJSON over the socket).
    let snapshotLine (w: World) : string =
        (buildSnapshot None w).ToJsonString(JsonSerializerOptions(WriteIndented = false))

    /// Pretty snapshot with an optional extra node spliced under "desktop".
    /// `None` is byte-identical to `snapshot` (existing consumers unaffected).
    let snapshotWith (extra: JsonObject option) (w: World) : string =
        (buildSnapshot extra w).ToJsonString(JsonSerializerOptions(WriteIndented = true))

    /// Compact snapshot with an optional extra node spliced under "desktop".
    /// `None` is byte-identical to `snapshotLine`.
    let snapshotLineWith (extra: JsonObject option) (w: World) : string =
        (buildSnapshot extra w).ToJsonString(JsonSerializerOptions(WriteIndented = false))

    // --- command parsing ---

    let private str (o: JsonNode) (key: string) =
        match o[key] with
        | null -> None
        | v -> Some(v.GetValue<string>())

    /// Parse "#rrggbb" / "rrggbb" / "#rgb" into (r, g, b) floats in 0..1.
    let hexColor (s: string) : (float * float * float) option =
        let h = s.TrimStart('#')
        let conv (hex: string) =
            float (System.Convert.ToInt32(hex, 16)) / 255.0
        try
            match h.Length with
            | 6 -> Some(conv (h.Substring(0, 2)), conv (h.Substring(2, 2)), conv (h.Substring(4, 2)))
            | 3 ->
                let dup (c: char) = conv (string c + string c)
                Some(dup h[0], dup h[1], dup h[2])
            | _ -> None
        with _ -> None

    let private selectorOf (o: JsonNode) =
        match str o "by", o["id"], str o "app" with
        | Some "next", _, _ -> NextWindow
        | Some "prev", _, _ -> PrevWindow
        | _, id, _ when not (isNull id) -> ById(id.GetValue<int>())
        | _, _, Some app -> ByApp app
        | _ -> Focused

    /// A request over the control socket: a read-only state query, or an action.
    type Request =
        | Query
        | Act of Command

    /// Parse one command object, e.g. {"cmd":"focus","by":"next"} or
    /// {"cmd":"layout","name":"bsp"}. Returns None on unknown/invalid input.
    let parse (json: string) : Command option =
        try
            let o = JsonNode.Parse json
            let flag (key: string) = (match o[key] with null -> false | v -> v.GetValue<bool>())
            let num (key: string) = (match o[key] with null -> None | v -> Some(v.GetValue<float>()))
            match str o "cmd" with
            | Some "focus" -> Some(Focus(selectorOf o))
            | Some "focusmaster" -> Some FocusMaster
            | Some "swap" ->
                match str o "dir" with
                | Some "prev" -> Some SwapPrev
                | Some "master" -> Some SwapMaster
                | _ -> Some SwapNext
            | Some "swapmaster" -> Some SwapMaster
            | Some "float" -> Some ToggleFloat
            | Some "fullscreen" -> Some ToggleFullscreen
            | Some "sinkall" -> Some SinkAll
            | Some "close" -> Some CloseFocused
            | Some "spawn" -> str o "run" |> Option.map Spawn
            | Some "workspace" ->
                match str o "switch", str o "move" with
                | Some t, _ -> Some(SwitchWorkspace t)
                | _, Some t -> Some(MoveToWorkspace t)
                | _ -> if flag "next" then Some NextWorkspace
                       elif flag "prev" then Some PrevWorkspace
                       else None
            | Some "layout" ->
                match str o "name" with
                | Some n -> Some(SetLayout n)
                | None -> if flag "next" then Some NextLayout else None
            | Some "master" ->
                if flag "inc" then Some IncMaster
                elif flag "dec" then Some DecMaster
                else match o["n"] with
                     | null -> None
                     | v -> Some(SetMaster(v.GetValue<int>()))
            | Some "ratio" -> num "value" |> Option.map SetRatio
            | Some "gaps" ->
                if flag "inc" then Some IncGaps
                elif flag "dec" then Some DecGaps
                else match o["value"] with
                     | null -> None
                     | v -> Some(SetGaps(v.GetValue<int>()))
            | Some "opacity" -> num "value" |> Option.map SetInactiveOpacity
            | Some "anim" -> num "value" |> Option.map SetAnimationSpeed
            | Some "border" ->
                match o["width"] with
                | null ->
                    match str o "color" |> Option.bind hexColor with
                    | Some(r, g, b) -> Some(SetBorderColor(flag "active", r, g, b))
                    | None -> None
                | v -> Some(SetBorderWidth(v.GetValue<int>()))
            | Some "corners" ->
                match o["value"] with
                | null -> None
                | v -> Some(SetCornerRadius(v.GetValue<int>()))
            | Some "blur" -> Some(SetBlur(flag "on"))
            | Some "undo" -> Some Undo
            | Some "redo" -> Some Redo
            | Some "session" ->
                if flag "save" then Some SaveSession
                elif flag "restore" then Some LoadSession
                else None
            | _ -> None
        with _ -> None

    /// Parse a control-socket line into a Request. A bare "state"/"snapshot"
    /// word or {"cmd":"state"} is a read-only Query; anything else is an Act.
    let parseRequest (line: string) : Request option =
        match line.Trim() with
        | "" | "state" | "snapshot" -> Some Query
        | t when t.StartsWith "{" ->
            match parse t with
            | Some cmd -> Some(Act cmd)
            | None ->
                try
                    match str (JsonNode.Parse t) "cmd" with
                    | Some "state" | Some "snapshot" -> Some Query
                    | _ -> None
                with _ -> None
        | _ -> None
