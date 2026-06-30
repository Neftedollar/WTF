module WTF.Core.Tests.AgentToolsTests

open System.Text.Json.Nodes
open Xunit
open WTF.Core
open WTF.Core.AgentTools

/// Parse a tiny args object literal for a tool call.
let private args (json: string) : JsonNode = JsonNode.Parse json

// --- manifest shape ---

[<Fact>]
let ``manifest has the 13 curated tools`` () =
    Assert.Equal(13, List.length manifest)

[<Fact>]
let ``manifest names are unique and non-empty`` () =
    let names = manifest |> List.map (fun t -> t.Name)
    Assert.DoesNotContain("", names)
    Assert.Equal(names.Length, (List.distinct names).Length)

[<Fact>]
let ``every tool is a well-formed JSON-Schema object`` () =
    for t in manifest do
        Assert.False(System.String.IsNullOrWhiteSpace t.Description)
        Assert.Equal("object", (t.Parameters["type"]).GetValue<string>())
        // properties + required are always present (required may be empty).
        Assert.NotNull(t.Parameters["properties"])
        Assert.NotNull(t.Parameters["required"])

[<Fact>]
let ``manifestJson is valid JSON, idempotent, and an array of name+parameters`` () =
    let a = manifestJson ()
    let b = manifestJson () // second call must not throw (no re-parenting)
    Assert.Equal(a, b)
    let arr = JsonNode.Parse a :?> JsonArray
    Assert.Equal(13, arr.Count)
    for node in arr do
        Assert.NotNull(node["name"])
        Assert.NotNull(node["description"])
        Assert.Equal("object", (node["parameters"]["type"]).GetValue<string>())

// --- mapping: every manifest tool resolves (totality / drift guard) ---

/// A representative valid args object for each tool name.
let private sampleArgs (name: string) : JsonNode =
    match name with
    | "focus_window" -> args """{"selector":"next"}"""
    | "set_layout" -> args """{"name":"bsp"}"""
    | "switch_workspace" -> args """{"tag":"3"}"""
    | "move_window_to_workspace" -> args """{"tag":"2"}"""
    | "spawn" -> args """{"command":"kitty"}"""
    | "set_ratio" -> args """{"value":0.6}"""
    | "set_master" -> args """{"n":2}"""
    | "notify" -> args """{"summary":"hi"}"""
    | _ -> args "{}" // the no-param tools

[<Fact>]
let ``toToolCall is total over the manifest`` () =
    for t in manifest do
        match toToolCall t.Name (sampleArgs t.Name) with
        | Some _ -> ()
        | None -> failwithf "tool %s did not resolve" t.Name

// --- mapping: correct Command / Notify results ---

[<Fact>]
let ``focus_window selectors map to the right Selector`` () =
    Assert.Equal(Some(ToCommand(Focus Focused)), toToolCall "focus_window" (args """{"selector":"focused"}"""))
    Assert.Equal(Some(ToCommand(Focus NextWindow)), toToolCall "focus_window" (args """{"selector":"next"}"""))
    Assert.Equal(Some(ToCommand(Focus PrevWindow)), toToolCall "focus_window" (args """{"selector":"prev"}"""))
    Assert.Equal(Some(ToCommand(Focus(ByApp "firefox"))), toToolCall "focus_window" (args """{"selector":"app","app":"firefox"}"""))
    Assert.Equal(Some(ToCommand(Focus(ById 7))), toToolCall "focus_window" (args """{"selector":"id","id":7}"""))

[<Fact>]
let ``simple tools map to their Commands`` () =
    Assert.Equal(Some(ToCommand(SetLayout "bsp")), toToolCall "set_layout" (args """{"name":"bsp"}"""))
    Assert.Equal(Some(ToCommand(SwitchWorkspace "3")), toToolCall "switch_workspace" (args """{"tag":"3"}"""))
    Assert.Equal(Some(ToCommand(MoveToWorkspace "2")), toToolCall "move_window_to_workspace" (args """{"tag":"2"}"""))
    Assert.Equal(Some(ToCommand NextWorkspace), toToolCall "next_workspace" (args "{}"))
    Assert.Equal(Some(ToCommand PrevWorkspace), toToolCall "prev_workspace" (args "{}"))
    Assert.Equal(Some(ToCommand(Spawn "kitty")), toToolCall "spawn" (args """{"command":"kitty"}"""))
    Assert.Equal(Some(ToCommand CloseFocused), toToolCall "close_focused" (args "{}"))
    Assert.Equal(Some(ToCommand(SetRatio 0.6)), toToolCall "set_ratio" (args """{"value":0.6}"""))
    Assert.Equal(Some(ToCommand(SetMaster 2)), toToolCall "set_master" (args """{"n":2}"""))
    Assert.Equal(Some(ToCommand ToggleFloat), toToolCall "toggle_float" (args "{}"))
    Assert.Equal(Some(ToCommand ToggleFullscreen), toToolCall "toggle_fullscreen" (args "{}"))

[<Fact>]
let ``notify maps to ToNotify with optional body`` () =
    Assert.Equal(Some(ToNotify("hi", "")), toToolCall "notify" (args """{"summary":"hi"}"""))
    Assert.Equal(Some(ToNotify("hi", "there")), toToolCall "notify" (args """{"summary":"hi","body":"there"}"""))

// --- mapping: bad / missing args rejected ---

[<Fact>]
let ``unknown tool name returns None`` () =
    Assert.Equal(None, toToolCall "explode" (args "{}"))

[<Fact>]
let ``missing required args return None`` () =
    Assert.Equal(None, toToolCall "focus_window" (args """{"selector":"app"}""")) // app missing
    Assert.Equal(None, toToolCall "focus_window" (args """{"selector":"id"}""")) // id missing
    Assert.Equal(None, toToolCall "focus_window" (args """{"selector":"bogus"}"""))
    Assert.Equal(None, toToolCall "set_layout" (args "{}"))
    Assert.Equal(None, toToolCall "switch_workspace" (args "{}"))
    Assert.Equal(None, toToolCall "spawn" (args "{}"))
    Assert.Equal(None, toToolCall "set_ratio" (args "{}"))
    Assert.Equal(None, toToolCall "set_master" (args "{}"))
    Assert.Equal(None, toToolCall "notify" (args "{}"))
