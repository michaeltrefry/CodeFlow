using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeFlow.Orchestration;

/// <summary>
/// Snapshot of what the saga would inject as <see cref="CodeFlow.Contracts.RetryContext"/> on
/// the next agent invocation after a Failed handoff. Both <see cref="WorkflowSagaStateMachine"/>
/// and <see cref="DryRun.DryRunExecutor"/> derive from this snapshot — saga serializes it to a
/// <see cref="CodeFlow.Contracts.RetryContext"/> for the published <c>AgentInvokeRequested</c>;
/// dry-run serializes it to a <see cref="JsonNode"/> diagnostic payload + a human-readable
/// message so authors can see the same data without invoking a model.
/// </summary>
public readonly record struct RetryContextSnapshot(
    int AttemptNumber,
    string? PriorFailureReason,
    string? PriorAttemptSummary);

/// <summary>
/// Builds a <see cref="RetryContextSnapshot"/> from a decision payload. Single source of truth
/// for the "what counts as a prior failure reason / attempt summary" rule, shared by saga and
/// dry-run. F-008 in the 2026-04-28 backend review.
/// </summary>
public interface IRetryContextBuilder
{
    /// <summary>
    /// Build a snapshot for the next handoff. <paramref name="attemptNumber"/> is the 1-based
    /// number of the *upcoming* attempt — caller decides whether that comes from saga decision
    /// history (saga) or a per-walk counter (dry-run).
    /// </summary>
    RetryContextSnapshot Build(int attemptNumber, JsonElement? decisionPayload);
}

public sealed class RetryContextBuilder : IRetryContextBuilder
{
    public RetryContextSnapshot Build(int attemptNumber, JsonElement? decisionPayload)
    {
        var (reason, summary) = ExtractFailureContext(decisionPayload);
        return new RetryContextSnapshot(attemptNumber, reason, summary);
    }

    /// <summary>
    /// Saga-canonical extraction. The decision payload published by AgentInvocationConsumer
    /// wraps the agent's submitted payload under <c>"payload"</c> and adds a sibling
    /// <c>"failure_context"</c> object whose own <c>"reason"</c> mirrors the agent's. Older
    /// code paths and tests sometimes emit reason at the top level, so check all three
    /// locations and prefer the most-specific available source.
    /// </summary>
    public static (string? Reason, string? Summary) ExtractFailureContext(JsonElement? payload)
    {
        if (payload is null || payload.Value.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }

        string? reason = null;
        if (payload.Value.TryGetProperty("failure_context", out var failureContextProbe)
            && failureContextProbe.ValueKind == JsonValueKind.Object
            && failureContextProbe.TryGetProperty("reason", out var fcReasonProperty)
            && fcReasonProperty.ValueKind == JsonValueKind.String)
        {
            reason = fcReasonProperty.GetString();
        }

        if (string.IsNullOrWhiteSpace(reason)
            && payload.Value.TryGetProperty("payload", out var nestedPayload)
            && nestedPayload.ValueKind == JsonValueKind.Object
            && nestedPayload.TryGetProperty("reason", out var nestedReasonProperty)
            && nestedReasonProperty.ValueKind == JsonValueKind.String)
        {
            reason = nestedReasonProperty.GetString();
        }

        if (string.IsNullOrWhiteSpace(reason)
            && payload.Value.TryGetProperty("reason", out var reasonProperty)
            && reasonProperty.ValueKind == JsonValueKind.String)
        {
            reason = reasonProperty.GetString();
        }

        if (!payload.Value.TryGetProperty("failure_context", out var failureContext)
            || failureContext.ValueKind != JsonValueKind.Object)
        {
            return (reason, null);
        }

        string? lastOutput = null;
        if (failureContext.TryGetProperty("last_output", out var lastOutputProperty)
            && lastOutputProperty.ValueKind == JsonValueKind.String)
        {
            lastOutput = lastOutputProperty.GetString();
        }

        int? toolCallsExecuted = null;
        if (failureContext.TryGetProperty("tool_calls_executed", out var toolCallsProperty)
            && toolCallsProperty.ValueKind == JsonValueKind.Number
            && toolCallsProperty.TryGetInt32(out var toolCalls))
        {
            toolCallsExecuted = toolCalls;
        }

        var summaryBuilder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(lastOutput))
        {
            summaryBuilder.Append("Last output: ").Append(lastOutput!.Trim());
        }

        if (toolCallsExecuted is { } calls)
        {
            if (summaryBuilder.Length > 0)
            {
                summaryBuilder.Append(Environment.NewLine);
            }

            summaryBuilder.Append("Tool calls executed: ").Append(calls);
        }

        var summary = summaryBuilder.Length == 0 ? null : summaryBuilder.ToString();
        return (reason, summary);
    }

    /// <summary>
    /// Convert a snapshot to the <see cref="CodeFlow.Contracts.RetryContext"/> payload published
    /// on the next <c>AgentInvokeRequested</c>. Saga-side adapter.
    /// </summary>
    public static CodeFlow.Contracts.RetryContext ToContract(RetryContextSnapshot snapshot) =>
        new(
            AttemptNumber: snapshot.AttemptNumber,
            PriorFailureReason: snapshot.PriorFailureReason,
            PriorAttemptSummary: snapshot.PriorAttemptSummary);

    /// <summary>
    /// Convert a snapshot to the <see cref="JsonNode"/> diagnostic-event payload the dry-run
    /// records on a <c>RetryContextHandoff</c> event. Dry-run-side adapter.
    /// </summary>
    public static JsonNode ToJsonNode(RetryContextSnapshot snapshot)
    {
        var node = new JsonObject
        {
            ["attemptNumber"] = snapshot.AttemptNumber,
        };
        if (snapshot.PriorFailureReason is not null)
        {
            node["priorFailureReason"] = snapshot.PriorFailureReason;
        }
        if (snapshot.PriorAttemptSummary is not null)
        {
            node["priorAttemptSummary"] = snapshot.PriorAttemptSummary;
        }
        return node;
    }

    /// <summary>
    /// Render a snapshot as the human-readable diagnostic message displayed on the dry-run
    /// retry-context-handoff event ("Saga would inject RetryContext: attempt #2. Reason: …").
    /// </summary>
    public static string ToMessage(RetryContextSnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.Append("Saga would inject RetryContext: attempt #").Append(snapshot.AttemptNumber).Append('.');
        if (!string.IsNullOrWhiteSpace(snapshot.PriorFailureReason))
        {
            builder.Append(" Reason: ").Append(snapshot.PriorFailureReason!.Trim()).Append('.');
        }
        if (!string.IsNullOrWhiteSpace(snapshot.PriorAttemptSummary))
        {
            builder.Append(" Summary: ").Append(snapshot.PriorAttemptSummary!.Trim());
        }
        return builder.ToString();
    }

    /// <summary>
    /// Re-parse a <see cref="JsonNode"/> as a <see cref="JsonElement"/> so dry-run callers
    /// (whose <c>DryRunMockResponse.Payload</c> is a <see cref="JsonNode"/>) can feed the
    /// builder using the same canonical input shape as the saga (<see cref="JsonElement"/>).
    /// </summary>
    public static JsonElement? AsJsonElement(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        var json = node.ToJsonString();
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
