namespace WTF.Core

open System.Text.Json
open System.Text.Json.Nodes

/// The agent-facing TOOL SURFACE: a curated, hand-written SUBSET of the Command
/// vocabulary, shaped as LLM tools (name + description + JSON-Schema params) plus
/// a pure, total mapping from a tool call back to a Command (or a host Notify
/// action). This module is deliberately AI-package-free and fully unit-testable
/// from WTF.Core.Tests — the LLM plumbing (Microsoft.Extensions.AI + Anthropic.SDK)
/// lives in WTF.Agent and merely consumes this manifest. There is ONE reducer
/// truth: `toToolCall` produces the SAME Command constructors the keybind /
/// Protocol paths use, so the agent and a keypress compile to the same intent.
module AgentTools =

    /// One agent tool: a name, a human/LLM description, and a JSON-Schema object
    /// describing its parameters (type:object + properties + required).
    type Tool =
        { Name: string
          Description: string
          Parameters: JsonObject }

    /// The result of resolving a tool call: either a pure World Command to be
    /// dispatched through the reducer, or a host-level Notify action (rendering /
    /// daemon work lives in WTF.Desktop, never in Core — the brain only carries
    /// the {summary, body} data).
    type ToolCall =
        | ToCommand of Command
        | ToNotify of summary: string * body: string

    // --- JSON-Schema builders (kept tiny; params stay primitive) ---

    let private strProp (desc: string) =
        let o = JsonObject()
        o["type"] <- JsonValue.Create "string"
        o["description"] <- JsonValue.Create desc
        o

    let private enumProp (desc: string) (values: string list) =
        let o = strProp desc
        let arr = JsonArray()
        for v in values do
            arr.Add(JsonValue.Create v)
        o["enum"] <- arr
        o

    let private intProp (desc: string) (minimum: int option) =
        let o = JsonObject()
        o["type"] <- JsonValue.Create "integer"
        o["description"] <- JsonValue.Create desc
        match minimum with
        | Some m -> o["minimum"] <- JsonValue.Create m
        | None -> ()
        o

    let private numProp (desc: string) (minimum: float) (maximum: float) =
        let o = JsonObject()
        o["type"] <- JsonValue.Create "number"
        o["description"] <- JsonValue.Create desc
        o["minimum"] <- JsonValue.Create minimum
        o["maximum"] <- JsonValue.Create maximum
        o

    /// Build a JSON-Schema object node from (name, prop) pairs + the required set.
    let private schema (props: (string * JsonObject) list) (required: string list) : JsonObject =
        let s = JsonObject()
        s["type"] <- JsonValue.Create "object"
        let p = JsonObject()
        for (k, v) in props do
            p[k] <- v
        s["properties"] <- p
        let req = JsonArray()
        for r in required do
            req.Add(JsonValue.Create r)
        s["required"] <- req
        s

    /// The curated agent tool manifest. Each entry maps 1:1 to a Command (or the
    /// host Notify action) via `toToolCall`. Params are primitive (string / integer
    /// / number / enum) so System.Text.Json schema handling stays trivial and any
    /// LLM can call them with zero SDK.
    let manifest: Tool list =
        [ { Name = "focus_window"
            Description =
                "Move keyboard focus to a window. Use selector=next/prev to cycle the stack, "
                + "selector=focused for the current one, selector=app with app=<appId> to focus a "
                + "specific application, or selector=id with id=<windowId> for an exact window."
            Parameters =
                schema
                    [ "selector", enumProp "How to point at the window." [ "focused"; "next"; "prev"; "app"; "id" ]
                      "app", strProp "Application id (required when selector=app)."
                      "id", intProp "Window id (required when selector=id)." None ]
                    [ "selector" ] }

          { Name = "set_layout"
            Description = "Set the current workspace's tiling layout by name."
            Parameters =
                schema [ "name", enumProp "Layout name." [ "tall"; "wide"; "bsp"; "grid"; "full" ] ] [ "name" ] }

          { Name = "switch_workspace"
            Description = "View another workspace by its tag (e.g. \"1\".. \"9\")."
            Parameters = schema [ "tag", strProp "Workspace tag to switch to." ] [ "tag" ] }

          { Name = "move_window_to_workspace"
            Description = "Send the focused window to another workspace by tag."
            Parameters = schema [ "tag", strProp "Destination workspace tag." ] [ "tag" ] }

          { Name = "next_workspace"
            Description = "View the next workspace in tag order (wraps)."
            Parameters = schema [] [] }

          { Name = "prev_workspace"
            Description = "View the previous workspace in tag order (wraps)."
            Parameters = schema [] [] }

          { Name = "spawn"
            Description = "Launch a program by its shell command line."
            Parameters = schema [ "command", strProp "Command to launch (e.g. \"kitty\")." ] [ "command" ] }

          { Name = "close_focused"
            Description = "Close the currently focused window."
            Parameters = schema [] [] }

          { Name = "set_ratio"
            Description = "Set the master area split ratio (clamped to 0.1..0.9)."
            Parameters = schema [ "value", numProp "Master/stack split ratio." 0.1 0.9 ] [ "value" ] }

          { Name = "set_master"
            Description = "Set how many windows occupy the master area (>= 1)."
            Parameters = schema [ "n", intProp "Number of master windows." (Some 1) ] [ "n" ] }

          { Name = "toggle_float"
            Description = "Toggle floating/tiled state of the focused window."
            Parameters = schema [] [] }

          { Name = "toggle_fullscreen"
            Description = "Toggle fullscreen on the focused window."
            Parameters = schema [] [] }

          { Name = "notify"
            Description =
                "Send a desktop notification to the user through WTF's own notification daemon. "
                + "Use this to report back to the user (closing the agent->user loop)."
            Parameters =
                schema
                    [ "summary", strProp "Short notification title."
                      "body", strProp "Optional longer notification body." ]
                    [ "summary" ] } ]

    /// Serialize the manifest as a JSON array of {name, description, parameters}.
    /// Returned by the `{"tools":true}` socket verb so any external LLM can
    /// discover and drive WTF with zero hardcoding.
    let manifestJson () : string =
        let arr = JsonArray()
        for t in manifest do
            let o = JsonObject()
            o["name"] <- JsonValue.Create t.Name
            o["description"] <- JsonValue.Create t.Description
            // DeepClone so the manifest's own JsonObject is never re-parented
            // (a JsonNode can only live under one parent; this keeps the call
            // idempotent across repeated invocations).
            o["parameters"] <- t.Parameters.DeepClone()
            arr.Add o
        arr.ToJsonString(JsonSerializerOptions(WriteIndented = false))

    // --- pure, total tool-call -> Command/Notify mapping ---

    let private argStr (o: JsonNode) (key: string) : string option =
        match o with
        | null -> None
        | _ ->
            match o[key] with
            | null -> None
            | v -> (try Some(v.GetValue<string>()) with _ -> None)

    let private argInt (o: JsonNode) (key: string) : int option =
        match o with
        | null -> None
        | _ ->
            match o[key] with
            | null -> None
            | v -> (try Some(v.GetValue<int>()) with _ -> None)

    let private argNum (o: JsonNode) (key: string) : float option =
        match o with
        | null -> None
        | _ ->
            match o[key] with
            | null -> None
            | v -> (try Some(v.GetValue<float>()) with _ -> None)

    /// Resolve a tool call (name + args object) to a Command or a Notify action.
    /// Total + pure: an unknown name, or a missing/invalid required argument,
    /// returns None. Layout names are NOT pre-validated here (the reducer gates
    /// them against the registry), mirroring `Protocol.parse`.
    let toToolCall (name: string) (args: JsonNode) : ToolCall option =
        match name with
        | "focus_window" ->
            match argStr args "selector" with
            | Some "focused" -> Some(ToCommand(Focus Focused))
            | Some "next" -> Some(ToCommand(Focus NextWindow))
            | Some "prev" -> Some(ToCommand(Focus PrevWindow))
            | Some "app" -> argStr args "app" |> Option.map (fun a -> ToCommand(Focus(ByApp a)))
            | Some "id" -> argInt args "id" |> Option.map (fun i -> ToCommand(Focus(ById i)))
            | _ -> None
        | "set_layout" -> argStr args "name" |> Option.map (fun n -> ToCommand(SetLayout n))
        | "switch_workspace" -> argStr args "tag" |> Option.map (fun t -> ToCommand(SwitchWorkspace t))
        | "move_window_to_workspace" -> argStr args "tag" |> Option.map (fun t -> ToCommand(MoveToWorkspace t))
        | "next_workspace" -> Some(ToCommand NextWorkspace)
        | "prev_workspace" -> Some(ToCommand PrevWorkspace)
        | "spawn" -> argStr args "command" |> Option.map (fun c -> ToCommand(Spawn c))
        | "close_focused" -> Some(ToCommand CloseFocused)
        | "set_ratio" -> argNum args "value" |> Option.map (fun v -> ToCommand(SetRatio v))
        | "set_master" -> argInt args "n" |> Option.map (fun n -> ToCommand(SetMaster n))
        | "toggle_float" -> Some(ToCommand ToggleFloat)
        | "toggle_fullscreen" -> Some(ToCommand ToggleFullscreen)
        | "notify" ->
            match argStr args "summary" with
            | Some s -> Some(ToNotify(s, defaultArg (argStr args "body") ""))
            | None -> None
        | _ -> None
