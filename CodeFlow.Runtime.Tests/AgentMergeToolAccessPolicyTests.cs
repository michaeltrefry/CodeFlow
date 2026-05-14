using CodeFlow.Runtime.Authority;
using FluentAssertions;

namespace CodeFlow.Runtime.Tests;

/// <summary>
/// Epic 993 / NO-7: targeted coverage for <see cref="Agent.MergeToolAccessPolicy"/> — the
/// point where the authority envelope, when present, becomes the authoritative tool allowlist.
/// Once NO-7 threads the effective (role ∪ node-override) tool set into the envelope's Role
/// tier, a node-override tool present in <c>envelope.ToolGrants</c> must survive into the
/// policy; an envelope that omits a tool must still restrict it.
/// </summary>
public sealed class AgentMergeToolAccessPolicyTests
{
    private static readonly AgentInvocationConfiguration Config =
        new(Provider: "anthropic", Model: "claude-opus");

    private static ResolvedAgentTools Tools(params string[] names) =>
        new(names, Array.Empty<McpToolDefinition>(), EnableHostTools: true);

    private static WorkflowExecutionEnvelope EnvelopeWithGrants(params string[] toolNames) =>
        WorkflowExecutionEnvelope.NoOpinion with
        {
            ToolGrants = toolNames.Select(n => new ToolGrant(n, ToolGrant.CategoryHost)).ToArray()
        };

    [Fact]
    public void NoEnvelope_UsesResolvedToolNames()
    {
        var policy = Agent.MergeToolAccessPolicy(Tools("role_tool", "node_override_tool"), Config, envelope: null);

        policy.DenyAll.Should().BeFalse();
        policy.AllowsTool("role_tool").Should().BeTrue();
        policy.AllowsTool("node_override_tool").Should().BeTrue();
        policy.AllowsTool("ungranted_tool").Should().BeFalse();
    }

    [Fact]
    public void NoEnvelope_NoResolvedTools_AllowsAll()
    {
        var policy = Agent.MergeToolAccessPolicy(ResolvedAgentTools.Empty, Config, envelope: null);

        policy.AllowsTool("anything").Should().BeTrue();
    }

    [Fact]
    public void EnvelopeWithGrants_AllowsTheAdditiveOverrideTool_WhenEnvelopeCarriesIt()
    {
        // Post-NO-7 the envelope's Role tier is built from role ∪ additive, so the override
        // tool is present in envelope.ToolGrants and must survive into the policy.
        var policy = Agent.MergeToolAccessPolicy(
            Tools("role_tool", "node_override_tool"),
            Config,
            EnvelopeWithGrants("role_tool", "node_override_tool"));

        policy.DenyAll.Should().BeFalse();
        policy.AllowsTool("role_tool").Should().BeTrue();
        policy.AllowsTool("node_override_tool").Should().BeTrue();
    }

    [Fact]
    public void EnvelopeWithGrants_StillRestricts_WhenEnvelopeOmitsAToolThatResolvedToolsHas()
    {
        // The envelope remains authoritative when present: a tool the resolved set has but the
        // envelope (post-intersection) dropped must NOT be callable.
        var policy = Agent.MergeToolAccessPolicy(
            Tools("role_tool", "node_override_tool"),
            Config,
            EnvelopeWithGrants("role_tool"));

        policy.AllowsTool("role_tool").Should().BeTrue();
        policy.AllowsTool("node_override_tool").Should().BeFalse();
    }

    [Fact]
    public void EnvelopeWithEmptyGrants_DeniesAll()
    {
        var policy = Agent.MergeToolAccessPolicy(
            Tools("role_tool"),
            Config,
            WorkflowExecutionEnvelope.NoOpinion with { ToolGrants = Array.Empty<ToolGrant>() });

        policy.DenyAll.Should().BeTrue();
        policy.AllowsTool("role_tool").Should().BeFalse();
    }
}
