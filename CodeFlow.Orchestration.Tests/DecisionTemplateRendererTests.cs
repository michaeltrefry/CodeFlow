using System.Text.Json;
using CodeFlow.Orchestration;
using CodeFlow.Persistence;
using CodeFlow.Runtime;
using FluentAssertions;

namespace CodeFlow.Orchestration.Tests;

public sealed class DecisionTemplateRendererTests
{
    private static readonly IReadOnlyDictionary<string, JsonElement> EmptyVars =
        new Dictionary<string, JsonElement>(StringComparer.Ordinal);

    private static IDecisionTemplateRenderer NewRenderer() =>
        new DecisionTemplateRenderer(new ScribanTemplateRenderer());

    private static AgentConfig AgentWithTemplates(IReadOnlyDictionary<string, string>? templates)
    {
        var configuration = new AgentInvocationConfiguration(
            Provider: "anthropic",
            Model: "claude-3-5-sonnet",
            DecisionOutputTemplates: templates);
        return new AgentConfig(
            Key: "decision-renderer-test",
            Version: 1,
            Kind: AgentKind.Agent,
            Configuration: configuration,
            ConfigJson: "{}",
            CreatedAtUtc: DateTime.UtcNow,
            CreatedBy: null);
    }

    private static DecisionTemplateInputs Inputs(string portName, string outputText) =>
        new(
            DecisionName: portName,
            EffectivePortName: portName,
            OutputText: outputText,
            OutputJson: default,
            InputJson: null,
            ContextInputs: EmptyVars,
            WorkflowInputs: EmptyVars);

    [Fact]
    public void Render_NoTemplates_ReturnsSkipped()
    {
        var renderer = NewRenderer();
        var agent = AgentWithTemplates(null);

        var result = renderer.Render(agent, Inputs("Approved", "ok"), CancellationToken.None);

        result.Should().BeOfType<DecisionTemplateRenderResult.Skipped>();
    }

    [Fact]
    public void Render_ExactPortMatch_ReturnsRenderedText()
    {
        var renderer = NewRenderer();
        var agent = AgentWithTemplates(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Approved"] = "DECISION={{ decision }}; OUT={{ output }}"
        });

        var result = renderer.Render(
            agent,
            Inputs("Approved", "submission body"),
            CancellationToken.None);

        result.Should().BeOfType<DecisionTemplateRenderResult.Rendered>()
            .Which.Text.Should().Be("DECISION=Approved; OUT=submission body");
    }

    [Fact]
    public void Render_PortLookupIsCaseInsensitive()
    {
        var renderer = NewRenderer();
        var agent = AgentWithTemplates(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Approved"] = "x"
        });

        var result = renderer.Render(
            agent,
            Inputs("approved", "y"),
            CancellationToken.None);

        result.Should().BeOfType<DecisionTemplateRenderResult.Rendered>();
    }

    [Fact]
    public void Render_FallsBackToWildcard_WhenNoExactMatch()
    {
        var renderer = NewRenderer();
        var agent = AgentWithTemplates(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["*"] = "wildcard:{{ outputPortName }}"
        });

        var result = renderer.Render(
            agent,
            Inputs("Rejected", "body"),
            CancellationToken.None);

        result.Should().BeOfType<DecisionTemplateRenderResult.Rendered>()
            .Which.Text.Should().Be("wildcard:Rejected");
    }

    [Fact]
    public void Render_PrefersExactPortOverWildcard()
    {
        var renderer = NewRenderer();
        var agent = AgentWithTemplates(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Approved"] = "exact",
            ["*"] = "fallback"
        });

        var result = renderer.Render(
            agent,
            Inputs("Approved", "body"),
            CancellationToken.None);

        result.Should().BeOfType<DecisionTemplateRenderResult.Rendered>()
            .Which.Text.Should().Be("exact");
    }

    [Fact]
    public void Render_StructuredOutputJson_ExposedAsObjectInScope()
    {
        var renderer = NewRenderer();
        var agent = AgentWithTemplates(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["*"] = "name={{ output.name }}"
        });

        using var doc = JsonDocument.Parse("{\"name\":\"alice\"}");
        var inputs = new DecisionTemplateInputs(
            DecisionName: "Approved",
            EffectivePortName: "Approved",
            OutputText: doc.RootElement.GetRawText(),
            OutputJson: doc.RootElement.Clone(),
            InputJson: null,
            ContextInputs: EmptyVars,
            WorkflowInputs: EmptyVars);

        var result = renderer.Render(agent, inputs, CancellationToken.None);

        result.Should().BeOfType<DecisionTemplateRenderResult.Rendered>()
            .Which.Text.Should().Be("name=alice");
    }

    [Fact]
    public void Render_BadTemplateSyntax_ReturnsFailedWithReason()
    {
        var renderer = NewRenderer();
        var agent = AgentWithTemplates(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Approved"] = "{{ if true"
        });

        var result = renderer.Render(
            agent,
            Inputs("Approved", "body"),
            CancellationToken.None);

        result.Should().BeOfType<DecisionTemplateRenderResult.Failed>()
            .Which.Reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ResolveTemplate_NullOrEmpty_ReturnsNull()
    {
        DecisionTemplateRenderer.ResolveTemplate(null, "Approved").Should().BeNull();
        DecisionTemplateRenderer.ResolveTemplate(
            new Dictionary<string, string>(),
            "Approved").Should().BeNull();
    }
}
