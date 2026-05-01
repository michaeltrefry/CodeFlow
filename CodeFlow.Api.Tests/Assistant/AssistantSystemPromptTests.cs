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
    public void DefaultPrompt_DescribesCurrentCapabilitiesAndGaps()
    {
        // The prompt's "what you can / can't do" stanza is the assistant's self-awareness about
        // shipped vs unshipped slices. As features land it must be kept current — these checks
        // pin the current state so a future PR that lands HAA-10 etc. updates this test
        // intentionally, not by accident.
        var prompt = AssistantSystemPrompt.Default;
        prompt.Should().Contain("Inspect a trace's timeline",
            because: "HAA-4 + HAA-5 wired tools; the prompt must claim trace-introspection capability");
        prompt.Should().Contain("Draft a complete workflow package",
            because: "HAA-9 made drafting a real capability");
        prompt.Should().Contain("save_workflow_package",
            because: "HAA-10 wired the save tool; the prompt must teach the model to invoke it");
        prompt.Should().Contain("run_workflow",
            because: "HAA-11 wired the run tool; the prompt must teach the model to invoke it");
        prompt.Should().Contain("diagnose_trace",
            because: "HAA-12 wired the diagnosis tool; the prompt must teach the model to invoke it");
        prompt.Should().Contain("propose_replay_with_edit",
            because: "HAA-13 wired the replay-with-edit bridge; the prompt must teach the model to invoke it");
    }

    [Theory]
    [InlineData("schemaVersion")]
    [InlineData("codeflow.workflow-package.v1")]
    [InlineData("entryPoint")]
    [InlineData("agentRoleAssignments")]
    [InlineData("Embedding rule")]
    [InlineData("cf-workflow-package")]
    public void DefaultPrompt_DescribesPackageEmissionContract(string token)
    {
        // HAA-9 emission contract: the assistant must know the package schema's top-level
        // shape, the embedding rule (token economy — only embed entities being created/bumped,
        // existing refs resolve from the DB), and the fence language hint the chat UI looks for.
        AssistantSystemPrompt.Default.Should().Contain(token,
            because: $"HAA-9 emission contract requires the prompt to mention '{token}'");
    }

    [Fact]
    public void DefaultPrompt_DescribesRefinementBehavior()
    {
        // Acceptance criterion: refining mid-conversation produces a coherent updated package
        // without losing earlier decisions, and re-emits the FULL package, never deltas.
        AssistantSystemPrompt.Default.Should().Contain("never deltas",
            because: "the prompt must explicitly forbid emitting partial / diff packages on refinement");
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
