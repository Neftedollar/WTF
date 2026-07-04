namespace WTF.Core

open System.Text.Json.Nodes

/// Canonical, lossless session codec — distinct from `Protocol.snapshot`, which
/// is a denormalized *read view* (computed `arrange`, the environmental layout
/// registry, string-keyed windows) with no inverse. This module persists the
/// minimal canonical World — Current / Nmaster / Ratio / Gaps / Screen /
/// Workspaces / Windows — with an exact parser, and is versioned so a future
/// schema change fails closed (`ofJson -> None`) rather than silently corrupting
/// state. Pure: no IO. The host owns the disk boundary.
module Session =

    [<Literal>]
    let private Schema = "wtf-session"

    [<Literal>]
    let private Version = 1

    let private rectJson (r: Rect) =
        let o = JsonObject()
        o["x"] <- JsonValue.Create(int r.X)
        o["y"] <- JsonValue.Create(int r.Y)
        o["w"] <- JsonValue.Create(int r.Width)
        o["h"] <- JsonValue.Create(int r.Height)
        o :> JsonNode

    /// Serialize a World to canonical, lossless JSON.
    let toJson (w: World) : string =
        let root = JsonObject()
        root["schema"] <- JsonValue.Create Schema
        root["version"] <- JsonValue.Create Version
        root["current"] <- JsonValue.Create w.Current
        root["nmaster"] <- JsonValue.Create w.Nmaster
        root["ratio"] <- JsonValue.Create w.Ratio
        root["gaps"] <- JsonValue.Create w.Gaps
        root["screen"] <- rectJson w.Screen

        let workspaces = JsonArray()
        for ws in w.Workspaces do
            let wo = JsonObject()
            wo["tag"] <- JsonValue.Create ws.Tag
            wo["layout"] <- JsonValue.Create ws.Layout
            // Workspace TYPE (#5) + its serializable per-type state. Emitted so a
            // non-"stack" workspace and a stateful type's data survive a restore.
            wo["type"] <- JsonValue.Create ws.Type
            wo["state"] <- JsonValue.Create ws.State
            // Never serialize the Up/Down zipper literally: emit visual order +
            // the focused id; the split is fully recoverable on load.
            match ws.Stack with
            | Some s ->
                let so = JsonObject()
                let ids = JsonArray()
                for id in Stack.toList s do
                    ids.Add(JsonValue.Create id)
                so["windows"] <- ids
                so["focused"] <- JsonValue.Create s.Focus
                wo["stack"] <- so
            | None -> wo["stack"] <- null
            // Floating: an array of {id,x,y,w,h}; Fullscreen: an int or null.
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

        // A Map<int,_> is serialized as an array (id lives inside WindowInfo),
        // rebuilt with Map.ofList — cleaner than stringified int keys.
        let windows = JsonArray()
        for KeyValue(_, info) in w.Windows do
            let io = JsonObject()
            io["id"] <- JsonValue.Create info.Id
            io["appId"] <- JsonValue.Create info.AppId
            io["title"] <- JsonValue.Create info.Title
            io["floating"] <- JsonValue.Create info.Floating
            windows.Add io
        root["windows"] <- windows

        root.ToJsonString(System.Text.Json.JsonSerializerOptions(WriteIndented = true))

    /// Parse canonical JSON back into a World. Returns None on malformed input
    /// or a version/schema mismatch — never a partial World.
    let ofJson (json: string) : World option =
        try
            let o = JsonNode.Parse json
            let schemaOk =
                (match o["schema"] with
                 | null -> false
                 | v -> v.GetValue<string>() = Schema)
                && (match o["version"] with
                    | null -> false
                    | v -> v.GetValue<int>() = Version)
            if not schemaOk then
                None
            else
                let rect (n: JsonNode) =
                    Rect.create
                        (n["x"].GetValue<int>())
                        (n["y"].GetValue<int>())
                        (n["w"].GetValue<int>())
                        (n["h"].GetValue<int>())

                let workspaces =
                    [ for wn in o["workspaces"].AsArray() ->
                          let stack =
                              match wn["stack"] with
                              | null -> None
                              | sn ->
                                  let ids = [ for i in sn["windows"].AsArray() -> i.GetValue<int>() ]
                                  let focused = sn["focused"].GetValue<int>()
                                  // Fail CLOSED on a corrupt focus: a focused id absent from
                                  // the stack's windows means the persisted state is malformed
                                  // (Stack.focus would otherwise silently no-op to the head).
                                  // The failwith is caught by the outer try -> None.
                                  match Stack.ofList ids with
                                  | None -> None
                                  | Some s ->
                                      if List.contains focused ids then Some(Stack.focus focused s)
                                      else failwith "session: focused id not present in stack windows"
                          // Guard with null-checks so an OLD session json (without these
                          // additive keys) still loads with empty/None defaults.
                          let floating =
                              match wn["floating"] with
                              | null -> Map.empty
                              | fa -> [ for fn in fa.AsArray() -> fn["id"].GetValue<int>(), rect fn ] |> Map.ofList
                          let fullscreen =
                              match wn["fullscreen"] with
                              | null -> None
                              | v -> Some(v.GetValue<int>())
                          // Type/State are optional: a session written before #5
                          // (or by a "stack" workspace) omits them -> default
                          // "stack"/"" so old session files still load.
                          let wsType =
                              match wn["type"] with null -> "stack" | v -> v.GetValue<string>()
                          let wsState =
                              match wn["state"] with null -> "" | v -> v.GetValue<string>()
                          { Tag = wn["tag"].GetValue<string>()
                            Layout = wn["layout"].GetValue<string>()
                            Type = wsType
                            State = wsState
                            Stack = stack
                            Floating = floating
                            Fullscreen = fullscreen } ]

                let windows =
                    [ for wn in o["windows"].AsArray() ->
                          let info =
                              { Id = wn["id"].GetValue<int>()
                                AppId = wn["appId"].GetValue<string>()
                                Title = wn["title"].GetValue<string>()
                                Floating = wn["floating"].GetValue<bool>() }
                          info.Id, info ]

                Some
                    { Workspaces = workspaces
                      Current = o["current"].GetValue<string>()
                      Windows = Map.ofList windows
                      Screen = rect o["screen"]
                      Nmaster = o["nmaster"].GetValue<int>()
                      Ratio = o["ratio"].GetValue<float>()
                      Gaps = o["gaps"].GetValue<int>() }
        with _ ->
            None
