using System.Globalization;
using System.Text.Json;
using CodeFlow.Runtime;

namespace CodeFlow.Orchestration;

/// <summary>
/// Flattens the per-invocation prompt scope (context, workflow, review-loop bindings, input) to the
/// dotted-key Scriban variable dictionary the renderer expects. Shared between the runtime
/// invocation path (<see cref="AgentInvocationConsumer"/>) and the live prompt-template preview
/// endpoint so the preview renders against the exact same scope shape the model would see.
/// </summary>
public static class AgentPromptScopeBuilder
{
    public static IReadOnlyDictionary<string, string?> BuildContextVariables(
        IReadOnlyDictionary<string, JsonElement>? contextInputs)
    {
        var variables = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (contextInputs is null)
        {
            return variables;
        }

        foreach (var (key, value) in contextInputs)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            AddFlattened(variables, $"context.{key}", value);
        }

        return variables;
    }

    public static IReadOnlyDictionary<string, string?> BuildWorkflowVariables(
        IReadOnlyDictionary<string, JsonElement>? workflowContext)
    {
        var variables = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (workflowContext is null)
        {
            return variables;
        }

        foreach (var (key, value) in workflowContext)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            AddFlattened(variables, $"workflow.{key}", value);
        }

        return variables;
    }

    public static IReadOnlyDictionary<string, string?> BuildReviewLoopVariables(
        int? reviewRound,
        int? reviewMaxRounds,
        IReadOnlyDictionary<string, JsonElement>? workflowContext = null)
    {
        if (reviewRound is not int round || reviewMaxRounds is not int maxRounds)
        {
            return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        }

        var variables = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["round"] = round.ToString(CultureInfo.InvariantCulture),
            ["maxRounds"] = maxRounds.ToString(CultureInfo.InvariantCulture),
            ["isLastRound"] = maxRounds > 0 && round >= maxRounds ? "true" : "false"
        };

        if (workflowContext is not null
            && workflowContext.TryGetValue(RejectionHistoryAccumulator.WorkflowVariableKey, out var historyElement))
        {
            variables["rejectionHistory"] = historyElement.ValueKind switch
            {
                JsonValueKind.String => historyElement.GetString(),
                JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
                _ => historyElement.GetRawText(),
            };
        }
        else
        {
            variables["rejectionHistory"] = string.Empty;
        }

        return variables;
    }

    /// <summary>
    /// Top-level Swarm-context variables exposed to a contributor / synthesizer / coordinator's
    /// prompt template. Mirrors <see cref="BuildReviewLoopVariables"/>: each field becomes a
    /// flat top-level template variable. The renderer reads them as <c>{{ swarmPosition }}</c>,
    /// <c>{{ swarmEarlyTerminated }}</c>, etc. — matching the V2 hand-authored library entry's
    /// convention so prompts can be migrated cleanly.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> BuildSwarmVariables(
        CodeFlow.Contracts.SwarmInvocationContext? swarm)
    {
        var variables = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (swarm is null)
        {
            return variables;
        }

        if (swarm.Position is int position)
        {
            variables["swarmPosition"] = position.ToString(CultureInfo.InvariantCulture);
        }

        if (swarm.MaxN is int maxN)
        {
            variables["swarmMaxN"] = maxN.ToString(CultureInfo.InvariantCulture);
        }

        if (!string.IsNullOrWhiteSpace(swarm.Assignment))
        {
            variables["swarmAssignment"] = swarm.Assignment;
        }

        if (swarm.EarlyTerminated is bool earlyTerminated)
        {
            variables["swarmEarlyTerminated"] = earlyTerminated ? "true" : "false";
        }

        return variables;
    }

    /// <summary>
    /// Exposes the per-invocation tool-call + duration budget to the prompt template so authors
    /// can ground budget-aware guidance ("If you have used 70% of the allowed tool calls…") in
    /// real numbers instead of asking the model to estimate its own budget. <see cref="Budget"/>
    /// can be null when an agent doesn't override the runtime defaults; this method always
    /// resolves to <see cref="InvocationLoopBudget.Default"/> in that case so the template
    /// always sees a concrete number rather than an empty variable.
    ///
    /// Exposed keys:
    /// <list type="bullet">
    ///   <item><description><c>maxToolCalls</c> — hard cap on tool calls per invocation.</description></item>
    ///   <item><description><c>maxConsecutiveNonMutatingCalls</c> — cap on consecutive read-only tools before forcing a mutation or submit.</description></item>
    ///   <item><description><c>maxLoopDurationSeconds</c> — wall-clock ceiling.</description></item>
    ///   <item><description><c>softWarnRemaining</c> / <c>hardWarnRemaining</c> — thresholds at which the loop appends transcript trailers; useful for authoring prompts that align with the platform's nudge schedule.</description></item>
    /// </list>
    /// </summary>
    public static IReadOnlyDictionary<string, string?> BuildBudgetVariables(InvocationLoopBudget? budget)
    {
        var resolved = budget ?? InvocationLoopBudget.Default;
        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["maxToolCalls"] = resolved.MaxToolCalls.ToString(CultureInfo.InvariantCulture),
            ["maxConsecutiveNonMutatingCalls"] = resolved.MaxConsecutiveNonMutatingCalls.ToString(CultureInfo.InvariantCulture),
            ["maxLoopDurationSeconds"] = ((long)resolved.MaxLoopDuration.TotalSeconds).ToString(CultureInfo.InvariantCulture),
            ["softWarnRemaining"] = resolved.SoftWarnRemaining.ToString(CultureInfo.InvariantCulture),
            ["hardWarnRemaining"] = resolved.HardWarnRemaining.ToString(CultureInfo.InvariantCulture),
        };
    }

    public static IReadOnlyDictionary<string, string?> BuildInputVariables(string? input)
    {
        var variables = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(input))
        {
            return variables;
        }

        try
        {
            using var document = JsonDocument.Parse(input);

            switch (document.RootElement.ValueKind)
            {
                case JsonValueKind.Object:
                case JsonValueKind.Array:
                    AddFlattened(variables, "input", document.RootElement);
                    break;
            }
        }
        catch (JsonException)
        {
            // Non-JSON input flows through as a plain string via ContextAssembler's "input" key.
        }

        return variables;
    }

    /// <summary>
    /// Convenience wrapper that builds the merged variable scope from JSON-shaped inputs (the form
    /// the API receives). Used by the live prompt-template preview endpoint so it can render
    /// against a single flat dictionary without duplicating the merge logic.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> BuildAll(
        IReadOnlyDictionary<string, JsonElement>? workflow,
        IReadOnlyDictionary<string, JsonElement>? context,
        int? reviewRound,
        int? reviewMaxRounds,
        string? input,
        InvocationLoopBudget? budget = null)
    {
        var merged = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in BuildContextVariables(context)) merged[entry.Key] = entry.Value;
        foreach (var entry in BuildWorkflowVariables(workflow)) merged[entry.Key] = entry.Value;
        foreach (var entry in BuildReviewLoopVariables(reviewRound, reviewMaxRounds, workflow)) merged[entry.Key] = entry.Value;
        foreach (var entry in BuildBudgetVariables(budget)) merged[entry.Key] = entry.Value;
        foreach (var entry in BuildInputVariables(input)) merged[entry.Key] = entry.Value;
        return merged;
    }

    public static IReadOnlyDictionary<string, string?>? Merge(
        IReadOnlyDictionary<string, string?>? configured,
        params IReadOnlyDictionary<string, string?>[] sources)
    {
        var anyContent = (configured is { Count: > 0 })
            || sources.Any(s => s.Count > 0);
        if (!anyContent)
        {
            return configured;
        }

        var merged = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (configured is not null)
        {
            foreach (var entry in configured)
            {
                merged[entry.Key] = entry.Value;
            }
        }

        foreach (var source in sources)
        {
            foreach (var entry in source)
            {
                merged[entry.Key] = entry.Value;
            }
        }

        return merged;
    }

    private static void AddFlattened(
        IDictionary<string, string?> variables,
        string key,
        JsonElement value)
    {
        variables[key] = ToTemplateValue(value);

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    AddFlattened(variables, $"{key}.{property.Name}", property.Value);
                }
                break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in value.EnumerateArray())
                {
                    AddFlattened(variables, $"{key}.{index}", item);
                    index += 1;
                }
                break;
        }
    }

    private static string? ToTemplateValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null => null,
            JsonValueKind.Undefined => null,
            _ => value.GetRawText()
        };
    }
}
