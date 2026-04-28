using CodeFlow.Api.Assistant;
using FluentAssertions;

namespace CodeFlow.Api.Tests.Assistant;

/// <summary>
/// HAA-3 — System prompt + core knowledge layer. Beyond the manual-eval acceptance criterion
/// (the assistant correctly answers ~20 curated questions covering each concept), these tests
/// act as a structural lint: if a section is accidentally removed in a future PR, CI fails so
/// the omission is intentional rather than silent.
/// </summary>
public sealed class AssistantSystemPromptTests
{
    [Fact]
    public async Task DefaultProvider_ReturnsTheCuratedPrompt()
    {
        var provider = new DefaultAssistantSystemPromptProvider();
        var prompt = await provider.GetSystemPromptAsync();
        prompt.Should().Be(AssistantSystemPrompt.Default);
    }

    [Fact]
    public void DefaultPrompt_IsNonTrivialAndIdentifiesTheAssistant()
    {
        var prompt = AssistantSystemPrompt.Default;
        prompt.Length.Should().BeGreaterThan(2_000,
            because: "the curated prompt covers many concepts; a tiny prompt means a section was deleted");
        prompt.Should().Contain("CodeFlow assistant");
        prompt.Should().Contain("workflow-orchestration");
    }

    [Fact]
    public void DefaultPrompt_ReinforcesWorkflowsAreData()
    {
        // Critical reminder from project memory: the assistant should never tell users to edit
        // CodeFlow source code in order to author a workflow.
        AssistantSystemPrompt.Default.Should().Contain("Workflows are data");
    }

    [Theory]
    [InlineData("agents")]
    [InlineData("Scriban")]
    [InlineData("workflow")]
    [InlineData("Subflow")]
    [InlineData("ReviewLoop")]
    [InlineData("Swarm")]
    [InlineData("Transform")]
    [InlineData("HITL")]
    public void DefaultPrompt_CoversAuthoringPrimitives(string concept)
    {
        AssistantSystemPrompt.Default.Should().Contain(concept,
            because: $"acceptance criterion requires authoring concept '{concept}'");
    }

    [Theory]
    [InlineData("port")]
    [InlineData("Failed")]
    [InlineData("edge")]
    public void DefaultPrompt_CoversPortModel(string concept)
    {
        AssistantSystemPrompt.Default.Should().Contain(concept,
            because: $"acceptance criterion requires port-model concept '{concept}'");
    }

    [Theory]
    [InlineData("input script")]
    [InlineData("output script")]
    [InlineData("setInput")]
    [InlineData("setOutput")]
    [InlineData("getGlobal")]
    [InlineData("setGlobal")]
    [InlineData("1 MiB")]
    public void DefaultPrompt_CoversScriptingPrimitives(string concept)
    {
        AssistantSystemPrompt.Default.Should().Contain(concept,
            because: $"acceptance criterion requires scripting concept '{concept}'");
    }

    [Theory]
    [InlineData("trace")]
    [InlineData("saga")]
    [InlineData("replay")]
    [InlineData("drift")]
    [InlineData("token")]
    [InlineData("in-place agent edit")]
    [InlineData("code-aware")]
    [InlineData("working directory")]
    public void DefaultPrompt_CoversRuntimeConcepts(string concept)
    {
        AssistantSystemPrompt.Default.Should().ContainEquivalentOf(concept,
            because: $"acceptance criterion requires runtime concept '{concept}'");
    }

    [Fact]
    public void DefaultPrompt_StatesHaa3Limitations()
    {
        var prompt = AssistantSystemPrompt.Default;
        prompt.Should().Contain("HAA-3",
            because: "the prompt should be self-aware about what slice it ships with");
        prompt.Should().Contain("no live introspection",
            because: "the assistant must explicitly tell users it can't query DB / run workflows yet");
    }

    [Fact]
    public void DefaultPrompt_NeverPromisesDollarCosts()
    {
        // Project guardrail: CodeFlow tracks tokens only — never compute or display dollar costs.
        // Catch any future edit that would reintroduce cost language as a thing the assistant
        // computes (the prompt may instruct against it but should not present it as a feature).
        var prompt = AssistantSystemPrompt.Default.ToLowerInvariant();
        prompt.Should().NotContain("$");
        prompt.Should().NotContain("price per token");
        prompt.Should().NotContain("compute the cost");
        prompt.Should().NotContain("estimate the cost");
    }
}
