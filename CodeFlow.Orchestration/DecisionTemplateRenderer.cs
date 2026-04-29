using System.Text.Json;
using CodeFlow.Persistence;
using CodeFlow.Runtime;

namespace CodeFlow.Orchestration;

/// <summary>
/// Renders an agent's per-port decision-output template (with wildcard fallback) against the
/// shared <see cref="DecisionOutputTemplateContext"/> scope. Single source of truth shared by
/// <see cref="WorkflowSagaStateMachine"/> (production) and
/// <see cref="DryRun.DryRunExecutor"/> (dry-run). Authors edit Scriban semantics, fallback rules,
/// or include resolution in one place.
/// </summary>
public interface IDecisionTemplateRenderer
{
    /// <summary>
    /// Resolve the matching template (port-name first, then <c>"*"</c>) and render it.
    /// Returns <see cref="DecisionTemplateRenderResult.Skipped"/> when no template matches,
    /// <see cref="DecisionTemplateRenderResult.Failed"/> on a Scriban authoring error, or
    /// <see cref="DecisionTemplateRenderResult.Rendered"/> with the resolved text on success.
    /// The caller adapts the result to its side-effect (saga writes an override artifact;
    /// DryRun records a synthetic event).
    /// </summary>
    DecisionTemplateRenderResult Render(
        AgentConfig agentConfig,
        DecisionTemplateInputs inputs,
        CancellationToken cancellationToken);
}

public readonly record struct DecisionTemplateInputs(
    string DecisionName,
    string EffectivePortName,
    string OutputText,
    JsonElement OutputJson,
    JsonElement? InputJson,
    IReadOnlyDictionary<string, JsonElement> ContextInputs,
    IReadOnlyDictionary<string, JsonElement> WorkflowInputs);

public abstract record DecisionTemplateRenderResult
{
    private DecisionTemplateRenderResult() { }

    public sealed record Skipped : DecisionTemplateRenderResult
    {
        public static readonly Skipped Instance = new();
    }

    public sealed record Rendered(string Text) : DecisionTemplateRenderResult;

    public sealed record Failed(string Reason) : DecisionTemplateRenderResult;
}

public sealed class DecisionTemplateRenderer : IDecisionTemplateRenderer
{
    private readonly IScribanTemplateRenderer scriban;

    public DecisionTemplateRenderer(IScribanTemplateRenderer scriban)
    {
        this.scriban = scriban ?? throw new ArgumentNullException(nameof(scriban));
    }

    public DecisionTemplateRenderResult Render(
        AgentConfig agentConfig,
        DecisionTemplateInputs inputs,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agentConfig);

        var template = ResolveTemplate(
            agentConfig.Configuration.DecisionOutputTemplates,
            inputs.EffectivePortName);
        if (template is null)
        {
            return DecisionTemplateRenderResult.Skipped.Instance;
        }

        var scope = DecisionOutputTemplateContext.Build(
            decision: inputs.DecisionName,
            outputPortName: inputs.EffectivePortName,
            outputText: inputs.OutputText,
            outputJson: IsStructured(inputs.OutputJson) ? inputs.OutputJson : null,
            inputJson: inputs.InputJson,
            contextInputs: inputs.ContextInputs,
            workflowInputs: inputs.WorkflowInputs);

        try
        {
            var rendered = scriban.Render(template, scope, cancellationToken);
            return new DecisionTemplateRenderResult.Rendered(rendered);
        }
        catch (PromptTemplateException ex)
        {
            return new DecisionTemplateRenderResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// Resolve a per-port decision-output template, falling back to the wildcard <c>*</c> entry.
    /// Exposed for callers that need the lookup outside of a full Render call (e.g. the dry-run
    /// HITL preview path that uses a different scope builder).
    /// </summary>
    public static string? ResolveTemplate(
        IReadOnlyDictionary<string, string>? templates,
        string portName)
    {
        if (templates is null || templates.Count == 0)
        {
            return null;
        }

        foreach (var entry in templates)
        {
            if (string.Equals(entry.Key, portName, StringComparison.OrdinalIgnoreCase))
            {
                return entry.Value;
            }
        }

        return templates.TryGetValue("*", out var wildcard) ? wildcard : null;
    }

    private static bool IsStructured(JsonElement element) =>
        element.ValueKind is JsonValueKind.Object or JsonValueKind.Array;
}
