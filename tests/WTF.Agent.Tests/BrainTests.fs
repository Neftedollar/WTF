module WTF.Agent.Tests.BrainTests

open System
open System.Collections.Generic
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Xunit
open Microsoft.Extensions.AI
open WTF.Agent
open WTF.Core

/// A no-op tool dispatch for the construction tests — never invoked on the no-key
/// path (the brain is disabled before any tool can run), and required only to
/// satisfy `tryCreate`'s signature.
let private noDispatch (_: AgentTools.ToolCall) : string = ""

/// OPT-IN + GRACEFUL: with no ANTHROPIC_API_KEY the brain is disabled and
/// tryCreate yields None — exercised here with no network and no key.
[<Fact>]
let ``brain is disabled when ANTHROPIC_API_KEY is unset`` () =
    let prev = System.Environment.GetEnvironmentVariable "ANTHROPIC_API_KEY"
    System.Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null)
    try
        Assert.False(Brain.isEnabled ())
        Assert.True((Brain.tryCreate noDispatch).IsNone)
    finally
        System.Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", prev)

[<Fact>]
let ``model name defaults to a current sonnet`` () =
    let prev = System.Environment.GetEnvironmentVariable "WTF_AGENT_MODEL"
    System.Environment.SetEnvironmentVariable("WTF_AGENT_MODEL", null)
    try
        Assert.Equal("claude-sonnet-4-6", Brain.modelName ())
    finally
        System.Environment.SetEnvironmentVariable("WTF_AGENT_MODEL", prev)

[<Fact>]
let ``model name honours WTF_AGENT_MODEL`` () =
    let prev = System.Environment.GetEnvironmentVariable "WTF_AGENT_MODEL"
    System.Environment.SetEnvironmentVariable("WTF_AGENT_MODEL", "claude-opus-4-8")
    try
        Assert.Equal("claude-opus-4-8", Brain.modelName ())
    finally
        System.Environment.SetEnvironmentVariable("WTF_AGENT_MODEL", prev)

// =============================================================================
// Brain.Ask — driven by a fake IChatClient (the ctor is public) so the
// non-null reply contract + the system/user/options wiring are exercised with
// NO network and NO key.
// =============================================================================

/// A recording fake IChatClient: captures the messages + options it was handed,
/// and replies with a single assistant message carrying `responseText` (null =>
/// an assistant message whose Text resolves to empty, as a real tool-only turn).
type private RecordingChatClient(responseText: string) =
    let mutable lastMessages : IList<ChatMessage> = null
    let mutable lastOptions : ChatOptions = null
    member _.LastMessages = lastMessages
    member _.LastOptions = lastOptions
    interface IChatClient with
        member _.GetResponseAsync(messages, options, _ct) =
            lastMessages <- List<ChatMessage>(messages)
            lastOptions <- options
            let resp =
                if isNull (box responseText)
                then ChatResponse(ChatMessage(ChatRole.Assistant, (null: string)))
                else ChatResponse(ChatMessage(ChatRole.Assistant, responseText))
            Task.FromResult resp
        member _.GetStreamingResponseAsync(_messages, _options, _ct) =
            raise (NotImplementedException())
        member _.GetService(_serviceType, _serviceKey) = null
        member _.Dispose() = ()

let private emptyTools () : IList<AITool> = ResizeArray<AITool>() :> IList<AITool>

[<Fact>]
let ``Ask returns the model text verbatim when non-empty`` () =
    let client = new RecordingChatClient("did the thing")
    let brain = Brain.Brain(client :> IChatClient, emptyTools ())
    Assert.Equal("did the thing", (brain.Ask "{}" "do it").Result)

[<Theory>]
[<InlineData("")>]
[<InlineData(null)>]
let ``Ask returns the fallback summary when the model yields no text`` (text: string) =
    let client = new RecordingChatClient(text)
    let brain = Brain.Brain(client :> IChatClient, emptyTools ())
    Assert.Equal("(done — tools executed, no text reply)", (brain.Ask "{}" "do it").Result)

[<Fact>]
let ``Ask wires the snapshot, request, model id and tools into the chat call`` () =
    let prevModel = Environment.GetEnvironmentVariable "WTF_AGENT_MODEL"
    Environment.SetEnvironmentVariable("WTF_AGENT_MODEL", "claude-test-9")
    try
        let client = new RecordingChatClient("ok")
        let tools = emptyTools ()
        let brain = Brain.Brain(client :> IChatClient, tools)
        let snap = """{"marker":"SNAPSHOT_123"}"""
        (brain.Ask snap "please tile").Result |> ignore
        let msgs = client.LastMessages
        Assert.Equal(2, msgs.Count)
        Assert.Equal(ChatRole.System, msgs.[0].Role)
        Assert.Contains("SNAPSHOT_123", msgs.[0].Text)        // snapshot embedded in the system prompt
        Assert.Equal(ChatRole.User, msgs.[1].Role)
        Assert.Equal("please tile", msgs.[1].Text)            // user message == the nl request
        Assert.Equal("claude-test-9", client.LastOptions.ModelId)
        Assert.Same(tools, client.LastOptions.Tools)          // the exact registered tool list
    finally
        Environment.SetEnvironmentVariable("WTF_AGENT_MODEL", prevModel)

// =============================================================================
// ToolFunction — the AIFunction arg-marshaling shim (internal; visible via
// InternalsVisibleTo). Verifies JsonElement/primitive round-trip into the pure
// AgentTools.toToolCall dispatch, null-skipping, and the unmapped-name path.
// =============================================================================

let private toolNamed name = AgentTools.manifest |> List.find (fun t -> t.Name = name)

let private invoke (tf: Brain.ToolFunction) (args: AIFunctionArguments) : string =
    let vt = (tf :> AIFunction).InvokeAsync(args, CancellationToken.None)
    string (vt.AsTask().Result)

[<Fact>]
let ``ToolFunction maps a JsonElement string arg to the right ToolCall and propagates the dispatch result`` () =
    let captured = ref None
    let dispatch tc = captured.Value <- Some tc; "DISPATCHED"
    let tf = Brain.ToolFunction(toolNamed "switch_workspace", dispatch)
    let args = AIFunctionArguments()
    args.["tag"] <- box (JsonSerializer.SerializeToElement "3")
    Assert.Equal("DISPATCHED", invoke tf args)
    Assert.Equal(Some(AgentTools.ToCommand(SwitchWorkspace "3")), captured.Value)

[<Fact>]
let ``ToolFunction maps a JsonElement int arg through to a Command`` () =
    let captured = ref None
    let tf = Brain.ToolFunction(toolNamed "set_master", fun tc -> captured.Value <- Some tc; "ok")
    let args = AIFunctionArguments()
    args.["n"] <- box (JsonSerializer.SerializeToElement 3)
    invoke tf args |> ignore
    Assert.Equal(Some(AgentTools.ToCommand(SetMaster 3)), captured.Value)

[<Fact>]
let ``ToolFunction skips null arg values`` () =
    let captured = ref None
    let tf = Brain.ToolFunction(toolNamed "focus_window", fun tc -> captured.Value <- Some tc; "ok")
    let args = AIFunctionArguments()
    args.["selector"] <- box (JsonSerializer.SerializeToElement "focused")
    args.["app"] <- null                                       // null value must be skipped, not crash
    invoke tf args |> ignore
    Assert.Equal(Some(AgentTools.ToCommand(Focus Focused)), captured.Value)

[<Fact>]
let ``ToolFunction reports an unmapped tool name as an error result`` () =
    let bogus : AgentTools.Tool = { Name = "does_not_exist"; Description = "x"; Parameters = JsonObject() }
    let tf = Brain.ToolFunction(bogus, fun _ -> "SHOULD_NOT_DISPATCH")
    Assert.Contains("unmapped tool call: does_not_exist", invoke tf (AIFunctionArguments()))

[<Fact>]
let ``ToolFunction preserves a boxed primitive arg's type (regression: no stringify)`` () =
    // A NON-JsonElement boxed CLR int must stay a JSON number so AgentTools.argInt
    // can read it — the old code stringified it, dropping the argument to None.
    let captured = ref None
    let tf = Brain.ToolFunction(toolNamed "set_master", fun tc -> captured.Value <- Some tc; "ok")
    let args = AIFunctionArguments()
    args.["n"] <- box 4                                        // raw boxed int, NOT a JsonElement
    invoke tf args |> ignore
    Assert.Equal(Some(AgentTools.ToCommand(SetMaster 4)), captured.Value)

// =============================================================================
// tryCreate boundaries.
// =============================================================================

[<Fact>]
let ``brain is disabled when ANTHROPIC_API_KEY is empty (distinct from null)`` () =
    let prev = Environment.GetEnvironmentVariable "ANTHROPIC_API_KEY"
    Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "")
    try
        Assert.False(Brain.isEnabled ())
        Assert.True((Brain.tryCreate noDispatch).IsNone)
    finally
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", prev)

[<Fact>]
let ``tryCreate with a non-empty key builds a brain without touching the network`` () =
    let prev = Environment.GetEnvironmentVariable "ANTHROPIC_API_KEY"
    Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "sk-test-dummy-not-used")
    try
        Assert.True(Brain.isEnabled ())
        match Brain.tryCreate noDispatch with
        | Some _ -> ()                                         // constructed; no network at ctor time
        | None -> Assert.Fail "expected Some brain with a non-empty key"
    finally
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", prev)
