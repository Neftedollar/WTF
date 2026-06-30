namespace WTF.Agent

open System
open System.Collections.Generic
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.AI
open Anthropic.SDK
open WTF.Core

/// The opt-in, in-process LLM brain: the natural-language door behind the socket's
/// {"ask":"<nl>"} verb. It feeds the World snapshot as context + the curated
/// `WTF.Core.AgentTools` manifest as function-calling tools to an `IChatClient`
/// (Microsoft.Extensions.AI backed by Anthropic.SDK), lets the model call the
/// tools, dispatches the resulting Commands through a host-provided callback
/// (which marshals onto the loop thread via the LoopBridge), and replies with a
/// text summary. This is the showcase of "World as context, Command as tools".
///
/// OPT-IN + GRACEFUL: the brain is enabled only when ANTHROPIC_API_KEY is set;
/// absent => `tryCreate` returns None (logged) and the {"ask"} verb cleanly
/// reports "agent disabled", with nothing else affected. The pure tool manifest +
/// ToolCall mapping live in `WTF.Core.AgentTools` (AI-package-free and unit-tested
/// there); this module is the only part that touches the AI packages or the key.
module Brain =

    /// True iff the opt-in brain is configured (ANTHROPIC_API_KEY present).
    let isEnabled () : bool =
        match Environment.GetEnvironmentVariable "ANTHROPIC_API_KEY" with
        | null | "" -> false
        | _ -> true

    /// The model to drive, from WTF_AGENT_MODEL (default a current Claude Sonnet).
    let modelName () : string =
        match Environment.GetEnvironmentVariable "WTF_AGENT_MODEL" with
        | null | "" -> "claude-sonnet-4-6"
        | m -> m

    /// One curated tool, exposed to the LLM as a Microsoft.Extensions.AI
    /// `AIFunction`. Name / Description / JSON-Schema come straight from the pure
    /// `AgentTools.Tool`; invocation rebuilds the call args as a JsonObject, runs
    /// the SAME pure `AgentTools.toToolCall` mapping the keybind/Protocol paths
    /// use, and hands the resulting ToolCall to the host `dispatch` callback (which
    /// marshals Commands onto the loop thread / drives the notify daemon). All the
    /// AI-specific plumbing is isolated here; the brain carries no World policy.
    type internal ToolFunction(tool: AgentTools.Tool, dispatch: AgentTools.ToolCall -> string) =
        inherit AIFunction()
        // The pure schema (type:object + properties + required) reused verbatim.
        let schema = JsonSerializer.Deserialize<JsonElement>(tool.Parameters.ToJsonString())

        override _.Name = tool.Name
        override _.Description = tool.Description
        override _.JsonSchema = schema

        override _.InvokeCoreAsync(args: AIFunctionArguments, _ct: CancellationToken) : ValueTask<obj> =
            // Rebuild the model's arguments as a JsonObject so the pure mapping can
            // consume them exactly like a socket {"...":...} payload. The values
            // arrive as JsonElement (deserialized tool-call JSON); fall back to a
            // primitive JsonValue for anything else.
            let o = JsonObject()
            for kv in args do
                match kv.Value with
                | null -> ()
                | :? JsonElement as je -> (try o[kv.Key] <- JsonNode.Parse(je.GetRawText()) with _ -> ())
                // Preserve the boxed CLR primitive's REPRESENTATION: serialize by its
                // runtime type so an int/float/bool stays a JSON number/boolean (not a
                // JSON *string* — which would make AgentTools.argInt/argNum's
                // GetValue<int>/<float> throw and silently drop the argument).
                | v -> (try o[kv.Key] <- JsonNode.Parse(JsonSerializer.Serialize(v, v.GetType())) with _ -> ())
            let result =
                match AgentTools.toToolCall tool.Name o with
                | Some call -> dispatch call
                | None -> sprintf """{"error":"unmapped tool call: %s"}""" tool.Name
            ValueTask<obj>(box result)

    /// A constructed, ready-to-run brain. Holds the function-calling IChatClient
    /// (Anthropic-backed, wrapped in a FunctionInvokingChatClient that drives the
    /// call -> execute -> loop automatically) and the registered tools.
    type Brain(client: IChatClient, tools: IList<AITool>) =

        /// Run one natural-language request. Builds a system prompt, attaches the
        /// live World+desktop snapshot as context, exposes the curated tools, and
        /// returns the model's text summary. Async + slow (seconds): the host runs
        /// this OFF the loop thread; each tool call marshals back via `dispatch`.
        member _.Ask (snapshotJson: string) (nl: string) : Task<string> =
            task {
                let sys =
                    "You control a tiling Wayland window manager (WTF). "
                    + "The JSON below is the live World + desktop state (workspaces, windows, "
                    + "focus, layouts, geometry, and notifications/battery/network/players). "
                    + "Use the provided tools to fulfil the user's request, then reply with a "
                    + "short plain-text summary of what you did. Only call a tool when the "
                    + "request needs an action.\n\nCURRENT STATE:\n"
                    + snapshotJson
                let messages = ResizeArray<ChatMessage>()
                messages.Add(ChatMessage(ChatRole.System, sys))
                messages.Add(ChatMessage(ChatRole.User, nl))
                let options = ChatOptions(ModelId = modelName (), Tools = tools)
                let! resp = client.GetResponseAsync(messages, options, CancellationToken.None)
                // The model may finish on a tool call with no trailing assistant
                // text — resp.Text is null then; give the socket a non-null summary.
                return
                    match resp.Text with
                    | null | "" -> "(done — tools executed, no text reply)"
                    | t -> t
            }

    /// Opt-in + graceful construction. Returns `None` when ANTHROPIC_API_KEY is
    /// unset (the brain is disabled and the {"ask"} verb cleanly reports so) or if
    /// the IChatClient fails to build. Otherwise builds the lazy Anthropic-backed
    /// `IChatClient` (`AnthropicClient(...).Messages :> IChatClient`, wrapped with
    /// `UseFunctionInvocation()` so tool calls run automatically) and registers the
    /// curated `AgentTools.manifest` as AIFunctions whose invocation routes through
    /// `dispatch`. No network is touched here — only at `Ask` time.
    let tryCreate (dispatch: AgentTools.ToolCall -> string) : Brain option =
        match Environment.GetEnvironmentVariable "ANTHROPIC_API_KEY" with
        | null | "" ->
            eprintfn "WTF agent: disabled (ANTHROPIC_API_KEY not set or empty)"
            None
        | key ->
            try
                let inner = (new AnthropicClient(APIAuthentication key)).Messages :> IChatClient
                let chat = ChatClientBuilder(inner).UseFunctionInvocation().Build()
                let tools : IList<AITool> =
                    AgentTools.manifest
                    |> List.map (fun t -> ToolFunction(t, dispatch) :> AITool)
                    |> List<AITool>
                    :> IList<AITool>
                eprintfn "WTF agent: enabled (model=%s, %d tools)" (modelName ()) tools.Count
                Some(Brain(chat, tools))
            with ex ->
                eprintfn "WTF agent: construction failed: %s" ex.Message
                None
