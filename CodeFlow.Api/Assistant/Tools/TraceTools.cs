using System.Text.Json;
using CodeFlow.Api.TokenTracking;
using CodeFlow.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CodeFlow.Api.Assistant.Tools;

/// <summary>
/// Trace-introspection tools (HAA-5). Mirrors the API's existing trace endpoints (ListTraces,
/// GetTrace, TraceTokenUsage) so authoring conventions and field names line up — the assistant
/// gets the same shape an authoring UI would, but bounded for LLM context.
/// </summary>
file static class TraceDefaults
{
    public const int DefaultListLimit = 25;
    public const int MaxListLimit = 100;

    /// <summary>Per-decision-payload truncation cap. Decision payloads are rarely large but
    /// can carry full agent outputs in some node kinds — bound them to keep the trace detail
    /// well below the dispatcher's 32 KB result budget.</summary>
    public const int LongStringCap = 4096;

    /// <summary>Per-IO-artifact truncation cap. Node inputs/outputs are typically the heaviest
    /// fields; cap each at 8 KB so a get_node_io call returns under the dispatcher budget even
    /// when both sides of the pair are present.</summary>
    public const int IoArtifactCap = 8192;
}

/// <summary>
/// Lists traces, optionally filtered by workflow key, current state, or "since" timestamp. Sorted
/// by most-recently-updated.
/// </summary>
public sealed class ListTracesTool(CodeFlowDbContext dbContext) : IAssistantTool
{
    public string Name => "list_traces";
    public string Description =>
        "List recent traces (most recently updated first). Returns each trace's id, workflow " +
        "key+version, current state ('Running'|'Completed'|'Failed'|...), round count, " +
        "createdAt/updatedAt UTC, parent trace id (for subflow children), failure reason, and " +
        "round id. Supports optional filtering by workflowKey (exact), state (exact), and " +
        "sinceUtc (ISO 8601 — only traces updated at or after this timestamp). Default limit 25, " +
        "max 100.";

    public JsonElement InputSchema => AssistantToolJson.Schema(@"{
        ""type"": ""object"",
        ""properties"": {
            ""workflowKey"": { ""type"": ""string"", ""description"": ""Exact-match workflow key filter."" },
            ""state"": { ""type"": ""string"", ""description"": ""Exact-match saga state filter, e.g. 'Running', 'Completed', 'Failed', 'AwaitingHumanInput'."" },
            ""sinceUtc"": { ""type"": ""string"", ""description"": ""ISO 8601 UTC timestamp; returns only traces updated at or after this instant."" },
            ""limit"": { ""type"": ""integer"", ""description"": ""Max results. Default 25, max 100."" }
        },
        ""additionalProperties"": false
    }");

    public async Task<AssistantToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var workflowKey = AssistantToolJson.ReadOptionalString(arguments, "workflowKey");
        var state = AssistantToolJson.ReadOptionalString(arguments, "state");
        var sinceText = AssistantToolJson.ReadOptionalString(arguments, "sinceUtc");
        var limit = AssistantToolJson.ClampLimit(
            AssistantToolJson.ReadOptionalInt(arguments, "limit"),
            TraceDefaults.DefaultListLimit,
            TraceDefaults.MaxListLimit);

        DateTime? sinceUtc = null;
        if (sinceText is not null)
        {
            if (!DateTime.TryParse(sinceText, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed))
            {
                return new AssistantToolResult(
                    JsonSerializer.Serialize(new { error = $"Could not parse sinceUtc '{sinceText}' as a UTC timestamp. Use ISO 8601 (e.g. '2026-04-28T00:00:00Z')." }),
                    IsError: true);
            }
            sinceUtc = parsed;
        }

        IQueryable<WorkflowSagaStateEntity> query = dbContext.WorkflowSagas.AsNoTracking();
        if (workflowKey is not null) query = query.Where(s => s.WorkflowKey == workflowKey);
        if (state is not null) query = query.Where(s => s.CurrentState == state);
        if (sinceUtc is not null) query = query.Where(s => s.UpdatedAtUtc >= sinceUtc);

        var rows = await query
            .OrderByDescending(s => s.UpdatedAtUtc)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var summaries = rows
            .Select(s => new
            {
                traceId = s.TraceId,
                workflowKey = s.WorkflowKey,
                workflowVersion = s.WorkflowVersion,
                currentState = s.CurrentState,
                currentNodeId = s.CurrentNodeId == Guid.Empty ? (Guid?)null : s.CurrentNodeId,
                currentAgentKey = string.IsNullOrEmpty(s.CurrentAgentKey) ? null : s.CurrentAgentKey,
                roundCount = s.RoundCount,
                decisionCount = s.DecisionCount,
                logicEvaluationCount = s.LogicEvaluationCount,
                createdAtUtc = DateTime.SpecifyKind(s.CreatedAtUtc, DateTimeKind.Utc),
                updatedAtUtc = DateTime.SpecifyKind(s.UpdatedAtUtc, DateTimeKind.Utc),
                failureReason = AssistantToolJson.TruncateText(s.FailureReason, TraceDefaults.LongStringCap),
                parentTraceId = s.ParentTraceId,
                parentNodeId = s.ParentNodeId,
                subflowDepth = s.SubflowDepth,
            })
            .ToArray();

        var json = JsonSerializer.Serialize(new
        {
            count = summaries.Length,
            limit,
            traces = summaries,
        }, AssistantToolJson.SerializerOptions);

        return new AssistantToolResult(json);
    }
}

/// <summary>
/// Fetches a single trace's header + decisions + logic-evaluations. Decision payloads are
/// truncated; the full input/output artifacts are NOT inlined — call <c>get_node_io</c> for a
/// specific node when needed.
/// </summary>
public sealed class GetTraceTool(CodeFlowDbContext dbContext) : IAssistantTool
{
    public string Name => "get_trace";
    public string Description =>
        "Get a single trace's full state: header (workflow key/version, state, round count), " +
        "ordered decisions (per-node agent invocations and routing outcomes), and ordered logic " +
        "evaluations (input scripts). Decision payloads are truncated to 4 KB. The trace's " +
        "input/output artifacts are NOT inlined here — use get_node_io for specific node payloads. " +
        "Subflow children are NOT walked — call get_trace separately for each parentTraceId/childTraceId.";

    public JsonElement InputSchema => AssistantToolJson.Schema(@"{
        ""type"": ""object"",
        ""properties"": {
            ""traceId"": { ""type"": ""string"", ""description"": ""Trace id (GUID, required)."" }
        },
        ""required"": [""traceId""],
        ""additionalProperties"": false
    }");

    public async Task<AssistantToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        if (!TryReadTraceId(arguments, "traceId", out var traceId, out var error))
        {
            return error;
        }

        var saga = await dbContext.WorkflowSagas
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.TraceId == traceId, cancellationToken);

        if (saga is null)
        {
            return new AssistantToolResult(
                JsonSerializer.Serialize(new { error = $"Trace '{traceId}' not found." }),
                IsError: true);
        }

        var decisions = await dbContext.WorkflowSagaDecisions
            .AsNoTracking()
            .Where(d => d.SagaCorrelationId == saga.CorrelationId)
            .OrderBy(d => d.Ordinal)
            .ToListAsync(cancellationToken);

        var logicEvaluations = await dbContext.WorkflowSagaLogicEvaluations
            .AsNoTracking()
            .Where(e => e.SagaCorrelationId == saga.CorrelationId)
            .OrderBy(e => e.Ordinal)
            .ToListAsync(cancellationToken);

        var payload = new
        {
            traceId = saga.TraceId,
            workflowKey = saga.WorkflowKey,
            workflowVersion = saga.WorkflowVersion,
            currentState = saga.CurrentState,
            currentNodeId = saga.CurrentNodeId == Guid.Empty ? (Guid?)null : saga.CurrentNodeId,
            currentAgentKey = string.IsNullOrEmpty(saga.CurrentAgentKey) ? null : saga.CurrentAgentKey,
            currentRoundId = saga.CurrentRoundId,
            roundCount = saga.RoundCount,
            createdAtUtc = DateTime.SpecifyKind(saga.CreatedAtUtc, DateTimeKind.Utc),
            updatedAtUtc = DateTime.SpecifyKind(saga.UpdatedAtUtc, DateTimeKind.Utc),
            failureReason = AssistantToolJson.TruncateText(saga.FailureReason, TraceDefaults.LongStringCap),
            parentTraceId = saga.ParentTraceId,
            parentNodeId = saga.ParentNodeId,
            parentReviewRound = saga.ParentReviewRound,
            subflowDepth = saga.SubflowDepth,
            pinnedAgentVersions = saga.GetPinnedAgentVersions(),
            decisions = decisions
                .Select(d => new
                {
                    ordinal = d.Ordinal,
                    nodeId = d.NodeId,
                    agentKey = d.AgentKey,
                    agentVersion = d.AgentVersion,
                    decision = d.Decision,
                    outputPortName = d.OutputPortName,
                    decisionPayload = AssistantToolJson.TruncateText(d.DecisionPayloadJson, TraceDefaults.LongStringCap),
                    inputRef = d.InputRef,
                    outputRef = d.OutputRef,
                    roundId = d.RoundId,
                    nodeEnteredAtUtc = d.NodeEnteredAtUtc,
                    recordedAtUtc = DateTime.SpecifyKind(d.RecordedAtUtc, DateTimeKind.Utc),
                })
                .ToArray(),
            logicEvaluations = logicEvaluations
                .Select(e => new
                {
                    ordinal = e.Ordinal,
                    nodeId = e.NodeId,
                    outputPortName = e.OutputPortName,
                    durationMs = TimeSpan.FromTicks(e.DurationTicks).TotalMilliseconds,
                    failureKind = e.FailureKind,
                    failureMessage = AssistantToolJson.TruncateText(e.FailureMessage, TraceDefaults.LongStringCap),
                    recordedAtUtc = DateTime.SpecifyKind(e.RecordedAtUtc, DateTimeKind.Utc),
                })
                .ToArray(),
        };

        return new AssistantToolResult(JsonSerializer.Serialize(payload, AssistantToolJson.SerializerOptions));
    }

    internal static bool TryReadTraceId(JsonElement args, string field, out Guid traceId, out AssistantToolResult error)
    {
        traceId = Guid.Empty;
        error = null!;

        var raw = AssistantToolJson.ReadOptionalString(args, field);
        if (raw is null)
        {
            error = new AssistantToolResult(
                JsonSerializer.Serialize(new { error = $"`{field}` is required." }),
                IsError: true);
            return false;
        }

        if (!Guid.TryParse(raw, out var parsed))
        {
            error = new AssistantToolResult(
                JsonSerializer.Serialize(new { error = $"`{field}` value '{raw}' is not a valid GUID." }),
                IsError: true);
            return false;
        }

        traceId = parsed;
        return true;
    }
}

/// <summary>
/// Returns a per-node timeline derived from the decision rows: each agent invocation's
/// node-entered timestamp + recorded-at timestamp + duration. Useful for the diagnosis flow
/// (HAA-12) when the assistant has to identify the slowest or most-recently-failed node.
/// </summary>
public sealed class GetTraceTimelineTool(CodeFlowDbContext dbContext) : IAssistantTool
{
    public string Name => "get_trace_timeline";
    public string Description =>
        "Get a per-node timeline for a trace. Each entry is one decision row with the node id, " +
        "agent key, port (decision label), node-entered timestamp, recorded-at timestamp, and " +
        "duration in milliseconds. Sorted by ordinal (chronological order of state transitions). " +
        "Logic-evaluation entries are interleaved by recordedAtUtc so the assistant sees both " +
        "agent and routing-script timing.";

    public JsonElement InputSchema => AssistantToolJson.Schema(@"{
        ""type"": ""object"",
        ""properties"": {
            ""traceId"": { ""type"": ""string"", ""description"": ""Trace id (GUID, required)."" }
        },
        ""required"": [""traceId""],
        ""additionalProperties"": false
    }");

    public async Task<AssistantToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        if (!GetTraceTool.TryReadTraceId(arguments, "traceId", out var traceId, out var error))
        {
            return error;
        }

        var saga = await dbContext.WorkflowSagas
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.TraceId == traceId, cancellationToken);

        if (saga is null)
        {
            return new AssistantToolResult(
                JsonSerializer.Serialize(new { error = $"Trace '{traceId}' not found." }),
                IsError: true);
        }

        var decisions = await dbContext.WorkflowSagaDecisions
            .AsNoTracking()
            .Where(d => d.SagaCorrelationId == saga.CorrelationId)
            .OrderBy(d => d.Ordinal)
            .ToListAsync(cancellationToken);

        var logicEvaluations = await dbContext.WorkflowSagaLogicEvaluations
            .AsNoTracking()
            .Where(e => e.SagaCorrelationId == saga.CorrelationId)
            .OrderBy(e => e.Ordinal)
            .ToListAsync(cancellationToken);

        var entries = decisions
            .Select(d => new TimelineEntry(
                kind: "decision",
                nodeId: d.NodeId,
                agentKey: d.AgentKey,
                portOrDecision: d.OutputPortName ?? d.Decision,
                roundId: d.RoundId,
                startedAtUtc: d.NodeEnteredAtUtc,
                recordedAtUtc: DateTime.SpecifyKind(d.RecordedAtUtc, DateTimeKind.Utc),
                durationMs: d.NodeEnteredAtUtc is { } entered && entered != DateTime.MinValue
                    ? Math.Max(0, (DateTime.SpecifyKind(d.RecordedAtUtc, DateTimeKind.Utc) - DateTime.SpecifyKind(entered, DateTimeKind.Utc)).TotalMilliseconds)
                    : (double?)null,
                failureKind: null,
                failureMessage: null))
            .Concat(logicEvaluations.Select(e => new TimelineEntry(
                kind: "logic-evaluation",
                nodeId: e.NodeId,
                agentKey: null,
                portOrDecision: e.OutputPortName,
                roundId: e.RoundId,
                startedAtUtc: null,
                recordedAtUtc: DateTime.SpecifyKind(e.RecordedAtUtc, DateTimeKind.Utc),
                durationMs: TimeSpan.FromTicks(e.DurationTicks).TotalMilliseconds,
                failureKind: e.FailureKind,
                failureMessage: AssistantToolJson.TruncateText(e.FailureMessage, TraceDefaults.LongStringCap))))
            .OrderBy(t => t.recordedAtUtc)
            .ToArray();

        var payload = new
        {
            traceId = saga.TraceId,
            currentState = saga.CurrentState,
            createdAtUtc = DateTime.SpecifyKind(saga.CreatedAtUtc, DateTimeKind.Utc),
            updatedAtUtc = DateTime.SpecifyKind(saga.UpdatedAtUtc, DateTimeKind.Utc),
            count = entries.Length,
            entries,
        };

        return new AssistantToolResult(JsonSerializer.Serialize(payload, AssistantToolJson.SerializerOptions));
    }

    private sealed record TimelineEntry(
        string kind,
        Guid? nodeId,
        string? agentKey,
        string? portOrDecision,
        Guid roundId,
        DateTime? startedAtUtc,
        DateTime recordedAtUtc,
        double? durationMs,
        string? failureKind,
        string? failureMessage);
}

/// <summary>
/// Returns aggregated token usage for a trace: trace total + per-invocation + per-node + per-scope
/// rollups. Reuses <see cref="TokenUsageAggregator"/> so the shape matches the existing trace
/// inspector's token panel exactly.
/// </summary>
public sealed class GetTraceTokenUsageTool(ITokenUsageRecordRepository repository) : IAssistantTool
{
    public string Name => "get_trace_token_usage";
    public string Description =>
        "Get aggregated token usage for a trace: total + per-invocation + per-node + per-scope " +
        "rollups, plus per-(provider, model) breakdowns. Field totals are flattened from the raw " +
        "provider payload (e.g. 'input_tokens', 'output_tokens', 'cache_read_input_tokens'). Use " +
        "this when the user asks 'what's the token cost of trace X' or 'which node was the most " +
        "expensive'. Returns an empty rollup (no error) if the trace has no token records yet.";

    public JsonElement InputSchema => AssistantToolJson.Schema(@"{
        ""type"": ""object"",
        ""properties"": {
            ""traceId"": { ""type"": ""string"", ""description"": ""Trace id (GUID, required)."" }
        },
        ""required"": [""traceId""],
        ""additionalProperties"": false
    }");

    public async Task<AssistantToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        if (!GetTraceTool.TryReadTraceId(arguments, "traceId", out var traceId, out var error))
        {
            return error;
        }

        var records = await repository.ListByTraceAsync(traceId, cancellationToken);
        var aggregate = TokenUsageAggregator.Aggregate(traceId, records);

        // Drop the verbatim per-call records (which carry the raw provider Usage JsonElement) — the
        // assistant only needs the rollups for "explain this trace's costs". Including the raw
        // call list balloons the payload past the dispatcher cap on long traces and the LLM rarely
        // needs that level of detail.
        var payload = new
        {
            traceId = aggregate.TraceId,
            recordCount = records.Count,
            total = aggregate.Total,
            byInvocation = aggregate.ByInvocation,
            byNode = aggregate.ByNode,
            byScope = aggregate.ByScope,
        };

        return new AssistantToolResult(JsonSerializer.Serialize(payload, AssistantToolJson.SerializerOptions));
    }
}

/// <summary>
/// Returns the input + output payloads for a specific node in a trace. Reads the artifacts
/// pointed to by the most-recent decision row's <c>InputRef</c>/<c>OutputRef</c>; both are
/// truncated to 8 KB.
/// </summary>
public sealed class GetNodeIoTool(CodeFlowDbContext dbContext, IArtifactStore artifactStore) : IAssistantTool
{
    public string Name => "get_node_io";
    public string Description =>
        "Get the input and output payloads for a specific node in a trace. Reads the artifacts " +
        "from the most-recent decision row for this (traceId, nodeId). Each side is truncated to " +
        "8 KB with a marker. If the node ran multiple times (e.g. inside a ReviewLoop), this " +
        "returns the most recent run; pass round to pin a specific round. Returns an error if " +
        "neither an input nor an output artifact was recorded.";

    public JsonElement InputSchema => AssistantToolJson.Schema(@"{
        ""type"": ""object"",
        ""properties"": {
            ""traceId"": { ""type"": ""string"", ""description"": ""Trace id (GUID, required)."" },
            ""nodeId"": { ""type"": ""string"", ""description"": ""Node id (GUID, required)."" },
            ""roundId"": { ""type"": ""string"", ""description"": ""Optional round id (GUID) to pin to a specific run; defaults to most recent."" }
        },
        ""required"": [""traceId"", ""nodeId""],
        ""additionalProperties"": false
    }");

    public async Task<AssistantToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        if (!GetTraceTool.TryReadTraceId(arguments, "traceId", out var traceId, out var error))
        {
            return error;
        }
        if (!GetTraceTool.TryReadTraceId(arguments, "nodeId", out var nodeId, out var nodeError))
        {
            return nodeError;
        }
        Guid? pinnedRoundId = null;
        var roundIdRaw = AssistantToolJson.ReadOptionalString(arguments, "roundId");
        if (roundIdRaw is not null)
        {
            if (!Guid.TryParse(roundIdRaw, out var rid))
            {
                return new AssistantToolResult(
                    JsonSerializer.Serialize(new { error = $"`roundId` value '{roundIdRaw}' is not a valid GUID." }),
                    IsError: true);
            }
            pinnedRoundId = rid;
        }

        IQueryable<WorkflowSagaDecisionEntity> q = dbContext.WorkflowSagaDecisions
            .AsNoTracking()
            .Where(d => d.TraceId == traceId && d.NodeId == nodeId);
        if (pinnedRoundId is not null) q = q.Where(d => d.RoundId == pinnedRoundId);

        var decision = await q
            .OrderByDescending(d => d.Ordinal)
            .FirstOrDefaultAsync(cancellationToken);

        if (decision is null)
        {
            return new AssistantToolResult(
                JsonSerializer.Serialize(new { error = $"No decision row found for trace '{traceId}' node '{nodeId}'{(pinnedRoundId is null ? "" : $" round '{pinnedRoundId}'")}." }),
                IsError: true);
        }

        var input = await ReadArtifactAsync(decision.InputRef, cancellationToken);
        var output = await ReadArtifactAsync(decision.OutputRef, cancellationToken);

        if (input is null && output is null)
        {
            return new AssistantToolResult(
                JsonSerializer.Serialize(new { error = $"Decision row exists for node '{nodeId}' in trace '{traceId}' but neither InputRef nor OutputRef was recorded (e.g. start node or HITL pass-through)." }),
                IsError: true);
        }

        var payload = new
        {
            traceId,
            nodeId,
            ordinal = decision.Ordinal,
            agentKey = decision.AgentKey,
            agentVersion = decision.AgentVersion,
            decision = decision.Decision,
            outputPortName = decision.OutputPortName,
            roundId = decision.RoundId,
            recordedAtUtc = DateTime.SpecifyKind(decision.RecordedAtUtc, DateTimeKind.Utc),
            input,
            output,
        };

        return new AssistantToolResult(JsonSerializer.Serialize(payload, AssistantToolJson.SerializerOptions));
    }

    private async Task<object?> ReadArtifactAsync(string? uriString, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(uriString)) return null;
        if (!Uri.TryCreate(uriString, UriKind.Absolute, out var uri)) return null;

        try
        {
            await using var stream = await artifactStore.ReadAsync(uri, cancellationToken);
            using var reader = new StreamReader(stream);
            var content = await reader.ReadToEndAsync(cancellationToken);

            return new
            {
                uri = uriString,
                contentLength = content.Length,
                content = AssistantToolJson.TruncateText(content, TraceDefaults.IoArtifactCap),
            };
        }
        catch (FileNotFoundException)
        {
            return new { uri = uriString, error = "artifact missing on disk" };
        }
        catch (DirectoryNotFoundException)
        {
            return new { uri = uriString, error = "artifact missing on disk" };
        }
    }
}
