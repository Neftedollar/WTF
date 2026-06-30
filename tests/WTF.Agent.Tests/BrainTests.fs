module WTF.Agent.Tests.BrainTests

open Xunit
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
