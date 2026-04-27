using System.Text.Json;
using System.Text.Json.Nodes;
using CodeFlow.Persistence;

namespace CodeFlow.Orchestration.DryRun;

/// <summary>
/// Per-agent mock response consumed by the dry-run executor when an agent node is visited.
/// Mirrors the runtime <see cref="CodeFlow.Runtime.AgentDecision"/> shape in spirit, but
/// exposed as a plain DTO so fixtures can be authored as JSON without referencing runtime
/// internals.
/// </summary>
public sealed record DryRunMockResponse(
    string Decision,
    string? Output,
    JsonNode? Payload);

/// <summary>
/// One entry recorded by the executor as the workflow walks. Mirrors the saga's
/// decision/logic-eval rows in shape so a single trace-style viewer can render it.
/// </summary>
public sealed record DryRunEvent(
    int Ordinal,
    DryRunEventKind Kind,
    Guid NodeId,
    string NodeKind,
    string? AgentKey,
    string? PortName,
    string? Message,
    string? InputPreview,
    string? OutputPreview,
    int? ReviewRound,
    int? MaxRounds,
    int? SubflowDepth,
    string? SubflowKey,
    int? SubflowVersion,
    IReadOnlyList<string>? Logs,
    JsonNode? DecisionPayload);

public enum DryRunEventKind
{
    NodeEntered = 0,
    AgentMockApplied = 1,
    LogicEvaluated = 2,
    HitlSuspended = 3,
    EdgeTraversed = 4,
    SubflowEntered = 5,
    SubflowExited = 6,
    LoopIteration = 7,
    LoopExhausted = 8,
    WorkflowCompleted = 9,
    WorkflowFailed = 10,
    BuiltinApplied = 11,
    Diagnostic = 12,
    RetryContextHandoff = 13,
    TransformRendered = 14,
}

public enum DryRunTerminalState
{
    Completed = 0,
    HitlReached = 1,
    Failed = 2,
    StepLimitExceeded = 3,
}

/// <summary>
/// Form payload captured when a dry-run halts at a HITL node. The dry-run surfaces the raw input
/// artifact the form would receive plus, when an <c>IAgentConfigRepository</c> is wired, the
/// agent's <c>outputTemplate</c> (the legacy single-template form rendered client-side) and any
/// <c>decisionOutputTemplates</c> the saga would apply on submit. <see cref="RenderedFormPreview"/>
/// is a best-effort server render of <c>outputTemplate</c> against <c>{ input, context, workflow }</c>
/// with empty form-field values, so the author can spot template typos without launching a real
/// run. <see cref="RenderError"/> surfaces any Scriban render failure when the preview is rendered.
/// </summary>
public sealed record DryRunHitlPayload(
    Guid NodeId,
    string AgentKey,
    string? Input,
    string? OutputTemplate = null,
    IReadOnlyDictionary<string, string>? DecisionOutputTemplates = null,
    string? RenderedFormPreview = null,
    string? RenderError = null);

public sealed record DryRunResult(
    DryRunTerminalState State,
    string? TerminalPort,
    string? FailureReason,
    string? FinalArtifact,
    DryRunHitlPayload? HitlPayload,
    IReadOnlyDictionary<string, JsonElement> WorkflowVariables,
    IReadOnlyDictionary<string, JsonElement> ContextVariables,
    IReadOnlyList<DryRunEvent> Events)
{
    public static DryRunResult Failure(string reason) => new(
        State: DryRunTerminalState.Failed,
        TerminalPort: null,
        FailureReason: reason,
        FinalArtifact: null,
        HitlPayload: null,
        WorkflowVariables: new Dictionary<string, JsonElement>(StringComparer.Ordinal),
        ContextVariables: new Dictionary<string, JsonElement>(StringComparer.Ordinal),
        Events: Array.Empty<DryRunEvent>());
}

public sealed record DryRunRequest(
    string WorkflowKey,
    int? WorkflowVersion,
    string? StartingInput,
    IReadOnlyDictionary<string, IReadOnlyList<DryRunMockResponse>> MockResponses)
{
    /// <summary>
    /// Build a dry-run request from a stored fixture. Returns null inputs / empty mocks when the
    /// fixture's payload is malformed; callers should validate before executing.
    /// </summary>
    public static DryRunRequest FromFixture(
        WorkflowFixtureEntity fixture,
        int? workflowVersionOverride = null,
        string? inputOverride = null)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        var mocks = ParseMockResponses(fixture.MockResponsesJson);
        return new DryRunRequest(
            WorkflowKey: fixture.WorkflowKey,
            WorkflowVersion: workflowVersionOverride,
            StartingInput: inputOverride ?? fixture.StartingInput,
            MockResponses: mocks);
    }

    public static IReadOnlyDictionary<string, IReadOnlyList<DryRunMockResponse>> ParseMockResponses(
        string? mockResponsesJson)
    {
        if (string.IsNullOrWhiteSpace(mockResponsesJson))
        {
            return new Dictionary<string, IReadOnlyList<DryRunMockResponse>>(StringComparer.Ordinal);
        }

        var node = JsonNode.Parse(mockResponsesJson);
        if (node is not JsonObject root)
        {
            return new Dictionary<string, IReadOnlyList<DryRunMockResponse>>(StringComparer.Ordinal);
        }

        var result = new Dictionary<string, IReadOnlyList<DryRunMockResponse>>(StringComparer.Ordinal);
        foreach (var (agentKey, value) in root)
        {
            if (value is not JsonArray arr)
            {
                continue;
            }

            var responses = new List<DryRunMockResponse>(arr.Count);
            foreach (var entry in arr)
            {
                if (entry is not JsonObject obj)
                {
                    continue;
                }

                var decision = obj["decision"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(decision))
                {
                    continue;
                }

                var output = obj["output"]?.GetValue<string>();
                var payload = obj["payload"]?.DeepClone();
                responses.Add(new DryRunMockResponse(decision, output, payload));
            }

            if (responses.Count > 0)
            {
                result[agentKey] = responses;
            }
        }

        return result;
    }
}
