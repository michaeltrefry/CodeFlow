using System.Text.Json;
using CodeFlow.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CodeFlow.Api.Assistant.Tools;

/// <summary>
/// HAA-13: lets the assistant propose substitution edits and offer to replay a past trace
/// through the existing Replay-with-Edit feature, gated by a UI-side confirmation chip.
/// </summary>
/// <remarks>
/// Mirrors HAA-10 / HAA-11: the LLM-callable tool is read-only validation; the actual replay
/// is a direct UI POST to the existing <c>POST /api/traces/{id}/replay</c> endpoint after the
/// user clicks the chip. The tool resolves the trace, looks up the saga subtree's recorded
/// decisions, validates each proposed edit's (agentKey, ordinal) against those decisions,
/// flags unsupported substitution kinds (currently Swarm nodes per
/// <see cref="CodeFlow.Orchestration.DryRun.DryRunExecutor"/> v4), and returns a verdict the
/// chat UI uses to render the chip.
///
/// The split keeps the assistant's tool loop synchronous (no need to pause mid-stream for a
/// UI click) while ensuring (a) every replay is gated by an explicit human click, (b) the
/// replay path uses the same drift detection / mock extraction as a click-driven replay from
/// the trace inspector, and (c) the replay executes under the logged-in user's identity.
/// </remarks>
public sealed class ProposeReplayWithEditTool(CodeFlowDbContext dbContext) : IAssistantTool
{
    public string Name => "propose_replay_with_edit";

    public string Description =>
        "Propose substitution edits and offer to replay a past trace through Replay-with-Edit. " +
        "The tool runs validation only; it does NOT trigger the replay. The chat UI surfaces a " +
        "'Replay' confirmation chip — only the user clicking that chip starts the replay. " +
        "Required: `traceId` (the original trace) and `edits` (an array of substitutions). Each " +
        "edit names an `agentKey` and `ordinal` (1-based; which invocation of that agent in the " +
        "recorded trace) and supplies at least one of `decision`, `output`, or `payload`. " +
        "Optional: `force` (opt into a best-effort replay despite hard drift), " +
        "`workflowVersionOverride` (pin the replay to a specific workflow version). " +
        "If a verdict is `invalid` or `unsupported`, fix the edit shape (or tell the user why " +
        "the substitution can't be made) and re-invoke; do NOT call the tool again until the " +
        "edit list is corrected. After a successful preview, do NOT call this tool again or " +
        "take further action; wait for the user's next message.";

    public JsonElement InputSchema => AssistantToolJson.Schema(@"{
        ""type"": ""object"",
        ""properties"": {
            ""traceId"": {
                ""type"": ""string"",
                ""description"": ""The original trace's id (GUID). Use list_traces / get_trace if unknown.""
            },
            ""edits"": {
                ""type"": ""array"",
                ""description"": ""One entry per substitution. Each must target a recorded (agentKey, ordinal) pair in the trace.""
            },
            ""force"": {
                ""type"": ""boolean"",
                ""description"": ""When true, replay proceeds even if hard drift is detected against the current workflow definition. Default false.""
            },
            ""workflowVersionOverride"": {
                ""type"": ""integer"",
                ""description"": ""Pin the replay to a specific workflow version. Defaults to the original trace's version.""
            }
        },
        ""required"": [""traceId"", ""edits""],
        ""additionalProperties"": false
    }");

    public async Task<AssistantToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var traceIdRaw = AssistantToolJson.ReadOptionalString(arguments, "traceId");
        if (string.IsNullOrWhiteSpace(traceIdRaw))
        {
            return Error("Argument `traceId` is required.");
        }

        if (!Guid.TryParse(traceIdRaw, out var traceId))
        {
            return Error($"Argument `traceId` is not a valid GUID: '{traceIdRaw}'.");
        }

        // Decode the edits array into a typed shape we can validate. We don't reuse the API
        // ReplayEditDto here because the wire shape is forgiving in ways the tool shouldn't be —
        // the LLM's job is to construct a clean substitution list and we want to bounce vague
        // input back rather than silently round-trip it.
        var (edits, parseError) = ParseEdits(arguments);
        if (parseError is not null)
        {
            return BadRequest(parseError);
        }

        if (edits.Count == 0)
        {
            return BadRequest("Argument `edits` must contain at least one substitution. To replay " +
                "with no edits the user can use the trace inspector directly — the assistant tool " +
                "is for proposing real substitutions.");
        }

        var force = AssistantToolJson.ReadOptionalBool(arguments, "force", defaultValue: false);
        var workflowVersionOverride = AssistantToolJson.ReadOptionalInt(arguments, "workflowVersionOverride");

        var rootSaga = await dbContext.WorkflowSagas
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.TraceId == traceId, cancellationToken);
        if (rootSaga is null)
        {
            return new AssistantToolResult(JsonSerializer.Serialize(new
            {
                status = "trace_not_found",
                message = $"Trace '{traceId}' was not found. List recent traces with `list_traces` " +
                          "to find a valid one.",
            }, AssistantToolJson.SerializerOptions));
        }

        // Pull every recorded decision for the saga subtree (subflows / review loops). The replay
        // endpoint groups decisions per-agent ordinally across the whole subtree; the substitutions
        // it accepts use the same grouping, so we mirror the lookup here.
        var subtreeCorrelationIds = await CollectSubtreeCorrelationIdsAsync(rootSaga.TraceId, rootSaga.CorrelationId, cancellationToken);
        var decisions = await dbContext.WorkflowSagaDecisions
            .AsNoTracking()
            .Where(d => subtreeCorrelationIds.Contains(d.SagaCorrelationId))
            .OrderBy(d => d.AgentKey)
            .ThenBy(d => d.RecordedAtUtc)
            .ThenBy(d => d.Ordinal)
            .ToListAsync(cancellationToken);

        // Group by AgentKey, assigning a 1-based per-agent ordinal in record-order. This matches
        // the meaning of `ReplayEditDto.Ordinal` — the *Nth invocation of that agent in the trace*,
        // not the saga's own per-saga ordinal (which is what the entity carries).
        var perAgentDecisions = decisions
            .GroupBy(d => d.AgentKey, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Select((d, idx) => new RecordedDecisionShape(
                    AgentKey: d.AgentKey,
                    OrdinalPerAgent: idx + 1,
                    NodeId: d.NodeId,
                    OriginalDecision: d.Decision,
                    RoundId: d.RoundId,
                    OutputPortName: d.OutputPortName)).ToList(),
                StringComparer.Ordinal);

        // Validate every edit against the recorded decisions. Per HAA-13's acceptance criteria:
        // unsupported substitution kinds (Swarm) bounce back with a clear message; agent-key
        // mismatches and ordinal-out-of-range bounce as `invalid`.
        var validation = ValidateEdits(edits, perAgentDecisions);
        if (validation.Status != "preview_ok")
        {
            return new AssistantToolResult(JsonSerializer.Serialize(new
            {
                status = validation.Status,
                traceId = rootSaga.TraceId,
                workflowKey = rootSaga.WorkflowKey,
                workflowVersion = rootSaga.WorkflowVersion,
                errors = validation.Errors,
                message = validation.Message,
                recordedDecisions = SummarizeDecisions(perAgentDecisions),
            }, AssistantToolJson.SerializerOptions));
        }

        // preview_ok: surface enough context for the chip to render a meaningful confirmation
        // prompt and for the LLM to summarize the proposed change in chat. The actual replay
        // request shape lives in the tool arguments themselves — the chat-panel reads `traceId`
        // + `edits` + `force` from the call args and POSTs them to /api/traces/{id}/replay on
        // confirm, no enrichment needed here.
        var summary = new
        {
            status = "preview_ok",
            traceId = rootSaga.TraceId,
            workflowKey = rootSaga.WorkflowKey,
            workflowVersion = rootSaga.WorkflowVersion,
            workflowVersionOverride,
            force,
            edits = edits.Select(e => new
            {
                agentKey = e.AgentKey,
                ordinal = e.Ordinal,
                decision = e.Decision,
                hasOutputOverride = e.Output is not null,
                hasPayloadOverride = e.Payload is not null,
                originalDecision = perAgentDecisions[e.AgentKey][e.Ordinal - 1].OriginalDecision,
            }).ToArray(),
            recordedDecisions = SummarizeDecisions(perAgentDecisions),
            message = "Preview validated. The user must click the 'Replay' chip in chat to start " +
                      "the replay — do not call this tool again or take further action until the " +
                      "user responds.",
        };

        return new AssistantToolResult(JsonSerializer.Serialize(summary, AssistantToolJson.SerializerOptions));
    }

    /// <summary>
    /// Walk down through subflow / ReviewLoop child sagas via parent_correlation_id, bounded by
    /// the workflow saga state machine's <c>MaxSubflowDepth</c>. Mirrors the equivalent walk in
    /// <see cref="CodeFlow.Api.Endpoints.TracesReplayEndpoints"/> so the substitution validation
    /// sees exactly the decision set the replay endpoint will see.
    /// </summary>
    /// <summary>
    /// Mirror of <see cref="CodeFlow.Api.Endpoints.TracesReplayEndpoints"/>'s subtree walk:
    /// breadth-first via <c>parent_trace_id</c>, bounded by
    /// <see cref="CodeFlow.Orchestration.WorkflowSagaStateMachine.MaxSubflowDepth"/>. Returns the
    /// CorrelationIds the decisions table should be filtered by.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> CollectSubtreeCorrelationIdsAsync(
        Guid rootTraceId,
        Guid rootCorrelationId,
        CancellationToken cancellationToken)
    {
        var allCorrelationIds = new List<Guid> { rootCorrelationId };
        var currentLevelTraceIds = new List<Guid> { rootTraceId };

        for (var level = 0; level < CodeFlow.Orchestration.WorkflowSagaStateMachine.MaxSubflowDepth; level++)
        {
            if (currentLevelTraceIds.Count == 0)
            {
                break;
            }

            var parents = currentLevelTraceIds;
            var children = await dbContext.WorkflowSagas
                .AsNoTracking()
                .Where(s => s.ParentTraceId != null && parents.Contains(s.ParentTraceId!.Value))
                .Select(s => new { s.TraceId, s.CorrelationId })
                .ToListAsync(cancellationToken);

            if (children.Count == 0)
            {
                break;
            }

            allCorrelationIds.AddRange(children.Select(c => c.CorrelationId));
            currentLevelTraceIds = children.Select(c => c.TraceId).ToList();
        }

        return allCorrelationIds;
    }

    private (IReadOnlyList<ParsedEdit> Edits, string? Error) ParseEdits(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("edits", out var editsProp) || editsProp.ValueKind != JsonValueKind.Array)
        {
            return (Array.Empty<ParsedEdit>(), "Argument `edits` must be an array of substitution objects.");
        }

        var edits = new List<ParsedEdit>();
        var index = 0;
        foreach (var item in editsProp.EnumerateArray())
        {
            index++;
            if (item.ValueKind != JsonValueKind.Object)
            {
                return (edits, $"`edits[{index - 1}]` must be an object, got {item.ValueKind}.");
            }

            var agentKey = AssistantToolJson.ReadOptionalString(item, "agentKey");
            if (string.IsNullOrWhiteSpace(agentKey))
            {
                return (edits, $"`edits[{index - 1}].agentKey` is required.");
            }

            var ordinal = AssistantToolJson.ReadOptionalInt(item, "ordinal");
            if (ordinal is null)
            {
                return (edits, $"`edits[{index - 1}].ordinal` is required (1-based per-agent invocation).");
            }
            if (ordinal.Value < 1)
            {
                return (edits, $"`edits[{index - 1}].ordinal` must be >= 1, got {ordinal.Value}.");
            }

            var decision = AssistantToolJson.ReadOptionalString(item, "decision");
            var output = AssistantToolJson.ReadOptionalString(item, "output");
            var payloadElement = item.TryGetProperty("payload", out var payloadProp)
                && payloadProp.ValueKind != JsonValueKind.Null
                && payloadProp.ValueKind != JsonValueKind.Undefined
                    ? (JsonElement?)payloadProp.Clone()
                    : null;

            if (decision is null && output is null && payloadElement is null)
            {
                return (edits, $"`edits[{index - 1}]` must supply at least one of " +
                    "`decision`, `output`, or `payload`.");
            }

            edits.Add(new ParsedEdit(agentKey, ordinal.Value, decision, output, payloadElement));
        }

        return (edits, null);
    }

    private static EditValidation ValidateEdits(
        IReadOnlyList<ParsedEdit> edits,
        IReadOnlyDictionary<string, List<RecordedDecisionShape>> perAgent)
    {
        var errors = new List<string>();
        var unsupported = new List<string>();

        foreach (var edit in edits)
        {
            if (!perAgent.TryGetValue(edit.AgentKey, out var recorded))
            {
                errors.Add($"agentKey '{edit.AgentKey}' is not present in the trace's recorded decisions.");
                continue;
            }

            if (edit.Ordinal > recorded.Count)
            {
                errors.Add($"ordinal {edit.Ordinal} is out of range for agent '{edit.AgentKey}' " +
                    $"(only {recorded.Count} invocation(s) recorded).");
                continue;
            }

            // Synthetic subflow agent-keys (e.g., "$subflow$") are markers introduced by the
            // ReplayMockExtractor for child saga returns; they aren't user-editable and the
            // chat-driven flow shouldn't see them. Reject explicitly with a clear message rather
            // than letting the replay endpoint silently fail.
            if (edit.AgentKey.StartsWith("$", StringComparison.Ordinal))
            {
                unsupported.Add($"agentKey '{edit.AgentKey}' is a synthetic subflow marker and " +
                    "isn't user-editable. Substitute the underlying agent's output inside the " +
                    "child trace instead.");
            }
        }

        if (unsupported.Count > 0)
        {
            return new EditValidation(
                Status: "unsupported",
                Errors: unsupported.Concat(errors).ToArray(),
                Message: "One or more substitutions target a node kind Replay-with-Edit doesn't " +
                    "currently support. Adjust the edit list and re-invoke.");
        }

        if (errors.Count > 0)
        {
            return new EditValidation(
                Status: "invalid",
                Errors: errors.ToArray(),
                Message: "One or more edits don't match a recorded (agentKey, ordinal) pair in the " +
                    "trace. Check the recordedDecisions list and re-invoke with a corrected edits array.");
        }

        return new EditValidation("preview_ok", Array.Empty<string>(), null);
    }

    private static IReadOnlyList<object> SummarizeDecisions(
        IReadOnlyDictionary<string, List<RecordedDecisionShape>> perAgent)
    {
        return perAgent
            .SelectMany(kvp => kvp.Value)
            .OrderBy(d => d.AgentKey, StringComparer.Ordinal)
            .ThenBy(d => d.OrdinalPerAgent)
            .Select(d => (object)new
            {
                agentKey = d.AgentKey,
                ordinal = d.OrdinalPerAgent,
                originalDecision = d.OriginalDecision,
                outputPortName = d.OutputPortName,
                nodeId = d.NodeId,
            })
            .ToArray();
    }

    private static AssistantToolResult Error(string message) =>
        new(JsonSerializer.Serialize(new { error = message }, AssistantToolJson.SerializerOptions),
            IsError: true);

    private static AssistantToolResult BadRequest(string message) =>
        new(JsonSerializer.Serialize(new
        {
            status = "invalid",
            message,
        }, AssistantToolJson.SerializerOptions));

    private sealed record ParsedEdit(
        string AgentKey,
        int Ordinal,
        string? Decision,
        string? Output,
        JsonElement? Payload);

    private sealed record RecordedDecisionShape(
        string AgentKey,
        int OrdinalPerAgent,
        Guid? NodeId,
        string OriginalDecision,
        Guid RoundId,
        string? OutputPortName);

    private sealed record EditValidation(string Status, IReadOnlyList<string> Errors, string? Message);
}
