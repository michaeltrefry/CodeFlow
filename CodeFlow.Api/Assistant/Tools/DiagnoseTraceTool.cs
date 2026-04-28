using System.Text.Json;
using CodeFlow.Api.TokenTracking;
using CodeFlow.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CodeFlow.Api.Assistant.Tools;

/// <summary>
/// HAA-12: focused trace-diagnosis composition. Combines the data the existing HAA-5 read-only
/// trace tools already expose (saga header, decision timeline, logic evaluations, token usage)
/// into a single structured verdict the LLM can lean on instead of chaining four tool calls and
/// re-deriving anomaly arithmetic each time.
/// </summary>
/// <remarks>
/// The tool is read-only — it does NOT mutate. The LLM calls it once with a trace id (the trace
/// page's HAA-8 page-context injection lands the id automatically; explicit invocation works
/// from anywhere) and renders the result as the diagnosis answer (failing node, cause, evidence,
/// recommended next action with deep links into the existing trace inspector and agent editor).
///
/// Anomaly heuristics:
/// - <b>long_duration</b>: any decision or logic-eval whose <c>durationMs</c> exceeds 60_000 ms.
/// - <b>token_spike</b>: any node whose total tokens exceed either 3× the trace's per-node
///   median or an absolute 50_000-token ceiling.
/// - <b>logic_failure</b>: any logic evaluation row with a non-null <c>FailureKind</c> — surfaced
///   as both a failing node (when the saga ended Failed because of it) and an anomaly (when the
///   saga ended Completed despite a recovered logic failure).
/// </remarks>
public sealed class DiagnoseTraceTool(
    CodeFlowDbContext dbContext,
    ITokenUsageRecordRepository tokenUsageRepository) : IAssistantTool
{
    private const string FailedState = "Failed";
    private const long LongDurationMsThreshold = 60_000;
    private const long AbsoluteTokenSpikeThreshold = 50_000;
    private const double RelativeTokenSpikeMultiplier = 3.0;
    private const int LongStringCap = 4096;

    public string Name => "diagnose_trace";

    public string Description =>
        "Diagnose a single trace: identify failing nodes, surface anomalies (token spikes, long " +
        "durations, logic-script failures), and propose next actions with deep links into the " +
        "trace inspector and agent editor. Use this whenever the user asks 'why did this fail?', " +
        "'what went wrong?', 'explain this trace', or any open-ended diagnostic question about a " +
        "specific trace. On a trace page, the trace id is in the current-page-context block — pass " +
        "it directly. Returns even for completed (non-failed) traces to flag anomalies. Render the " +
        "answer as: one-sentence lead → failing node + cause + evidence → suggested next action " +
        "with the deep links the tool returns.";

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

        var tokenRecords = await tokenUsageRepository.ListByTraceAsync(traceId, cancellationToken);
        var tokenAggregate = TokenUsageAggregator.Aggregate(traceId, tokenRecords);

        var failingNodes = BuildFailingNodes(saga, decisions, logicEvaluations);
        var anomalies = BuildAnomalies(decisions, logicEvaluations, tokenAggregate);
        var tokenSummary = BuildTokenSummary(tokenAggregate);
        var suggestions = BuildSuggestions(saga, failingNodes, anomalies);

        var payload = new
        {
            traceId = saga.TraceId,
            workflowKey = saga.WorkflowKey,
            workflowVersion = saga.WorkflowVersion,
            currentState = saga.CurrentState,
            failureReason = AssistantToolJson.TruncateText(saga.FailureReason, LongStringCap),
            createdAtUtc = DateTime.SpecifyKind(saga.CreatedAtUtc, DateTimeKind.Utc),
            updatedAtUtc = DateTime.SpecifyKind(saga.UpdatedAtUtc, DateTimeKind.Utc),
            summary = BuildSummary(saga, failingNodes.Count, anomalies.Count),
            failingNodes,
            anomalies,
            tokenSummary,
            suggestions,
            links = new
            {
                trace = $"/traces/{saga.TraceId}",
                workflow = $"/workflows/{saga.WorkflowKey}/{saga.WorkflowVersion}",
            },
        };

        return new AssistantToolResult(JsonSerializer.Serialize(payload, AssistantToolJson.SerializerOptions));
    }

    private static string BuildSummary(WorkflowSagaStateEntity saga, int failingNodeCount, int anomalyCount)
    {
        if (string.Equals(saga.CurrentState, FailedState, StringComparison.Ordinal))
        {
            var reason = string.IsNullOrWhiteSpace(saga.FailureReason)
                ? "no recorded failure reason"
                : saga.FailureReason;
            return $"Trace ended in Failed state — {reason}.";
        }

        if (failingNodeCount > 0)
        {
            return $"Trace currently in '{saga.CurrentState}' but has {failingNodeCount} recorded failure(s) along the way.";
        }

        if (anomalyCount > 0)
        {
            return $"Trace in state '{saga.CurrentState}' completed without failures but {anomalyCount} anomaly(ies) flagged for review.";
        }

        return $"Trace in state '{saga.CurrentState}' has no failures or anomalies.";
    }

    private static List<object> BuildFailingNodes(
        WorkflowSagaStateEntity saga,
        IReadOnlyList<WorkflowSagaDecisionEntity> decisions,
        IReadOnlyList<WorkflowSagaLogicEvaluationEntity> logicEvaluations)
    {
        var result = new List<object>();
        var sagaFailed = string.Equals(saga.CurrentState, FailedState, StringComparison.Ordinal);

        // Logic evaluations with a non-null FailureKind always go on the failing-nodes list — a
        // routing-script error is a concrete signal regardless of whether the saga recovered.
        foreach (var eval in logicEvaluations.Where(e => !string.IsNullOrEmpty(e.FailureKind)))
        {
            result.Add(new
            {
                kind = "logic-evaluation",
                nodeId = eval.NodeId,
                roundId = eval.RoundId,
                failureKind = eval.FailureKind,
                failureMessage = AssistantToolJson.TruncateText(eval.FailureMessage, LongStringCap),
                recordedAtUtc = DateTime.SpecifyKind(eval.RecordedAtUtc, DateTimeKind.Utc),
                deepLink = $"/traces/{saga.TraceId}",
            });
        }

        // Decision rows whose Decision payload signals a failure. The runtime convention is that
        // the saga's terminal Failed state is reached after a node emits a "Failed" decision (or
        // hits the implicit Failed port); the most recent decision before the saga's terminal
        // FailureReason was written is the offender. We also include any decision whose
        // OutputPortName is "Failed" — those are explicit Failed-port routes.
        if (sagaFailed && decisions.Count > 0)
        {
            // Last decision before the terminal state — most informative for "what failed".
            var last = decisions[^1];
            result.Add(new
            {
                kind = "agent-decision",
                nodeId = last.NodeId,
                agentKey = last.AgentKey,
                agentVersion = last.AgentVersion,
                decision = last.Decision,
                outputPortName = last.OutputPortName,
                failureMessage = AssistantToolJson.TruncateText(saga.FailureReason, LongStringCap),
                recordedAtUtc = DateTime.SpecifyKind(last.RecordedAtUtc, DateTimeKind.Utc),
                deepLink = $"/traces/{saga.TraceId}",
                agentDeepLink = string.IsNullOrEmpty(last.AgentKey) ? null : $"/agents/{last.AgentKey}",
            });
        }

        // Any decision routed to an explicit "Failed" port that ISN'T already the terminal one.
        foreach (var d in decisions)
        {
            if (string.Equals(d.OutputPortName, "Failed", StringComparison.Ordinal)
                && !(sagaFailed && d.Ordinal == decisions[^1].Ordinal))
            {
                result.Add(new
                {
                    kind = "agent-decision",
                    nodeId = d.NodeId,
                    agentKey = d.AgentKey,
                    agentVersion = d.AgentVersion,
                    decision = d.Decision,
                    outputPortName = d.OutputPortName,
                    failureMessage = AssistantToolJson.TruncateText(d.DecisionPayloadJson, LongStringCap),
                    recordedAtUtc = DateTime.SpecifyKind(d.RecordedAtUtc, DateTimeKind.Utc),
                    deepLink = $"/traces/{saga.TraceId}",
                    agentDeepLink = string.IsNullOrEmpty(d.AgentKey) ? null : $"/agents/{d.AgentKey}",
                });
            }
        }

        return result;
    }

    private static List<object> BuildAnomalies(
        IReadOnlyList<WorkflowSagaDecisionEntity> decisions,
        IReadOnlyList<WorkflowSagaLogicEvaluationEntity> logicEvaluations,
        Dtos.TraceTokenUsageDto tokenAggregate)
    {
        var anomalies = new List<object>();

        // long_duration on agent decisions
        foreach (var d in decisions)
        {
            if (d.NodeEnteredAtUtc is { } entered && entered != DateTime.MinValue)
            {
                var duration = (DateTime.SpecifyKind(d.RecordedAtUtc, DateTimeKind.Utc)
                    - DateTime.SpecifyKind(entered, DateTimeKind.Utc)).TotalMilliseconds;
                if (duration > LongDurationMsThreshold)
                {
                    anomalies.Add(new
                    {
                        kind = "long_duration",
                        severity = duration > LongDurationMsThreshold * 5 ? "high" : "medium",
                        nodeId = d.NodeId,
                        agentKey = d.AgentKey,
                        evidence = new { durationMs = (long)duration, thresholdMs = LongDurationMsThreshold },
                        message = $"Agent decision at node {d.NodeId} took {(long)duration}ms (>{LongDurationMsThreshold}ms threshold).",
                    });
                }
            }
        }

        // long_duration on logic evaluations
        foreach (var eval in logicEvaluations)
        {
            var ms = TimeSpan.FromTicks(eval.DurationTicks).TotalMilliseconds;
            if (ms > LongDurationMsThreshold)
            {
                anomalies.Add(new
                {
                    kind = "long_duration",
                    severity = ms > LongDurationMsThreshold * 5 ? "high" : "medium",
                    nodeId = (Guid?)eval.NodeId,
                    agentKey = (string?)null,
                    evidence = new { durationMs = (long)ms, thresholdMs = LongDurationMsThreshold, kind = "logic-evaluation" },
                    message = $"Logic evaluation at node {eval.NodeId} took {(long)ms}ms (>{LongDurationMsThreshold}ms threshold).",
                });
            }
        }

        // token_spike: per-node total > max(absolute_threshold, multiplier × per-node median)
        if (tokenAggregate.ByNode.Count > 0)
        {
            var nodeTotals = tokenAggregate.ByNode
                .Select(n => new
                {
                    n.NodeId,
                    Total = n.Rollup.Totals.Values.Sum(),
                })
                .Where(n => n.Total > 0)
                .OrderBy(n => n.Total)
                .ToArray();

            if (nodeTotals.Length > 0)
            {
                var median = nodeTotals[nodeTotals.Length / 2].Total;
                var spikeThreshold = Math.Max(AbsoluteTokenSpikeThreshold, (long)(median * RelativeTokenSpikeMultiplier));

                foreach (var n in nodeTotals.Where(n => n.Total > spikeThreshold))
                {
                    anomalies.Add(new
                    {
                        kind = "token_spike",
                        severity = n.Total > spikeThreshold * 2 ? "high" : "medium",
                        nodeId = (Guid?)n.NodeId,
                        agentKey = (string?)null,
                        evidence = new
                        {
                            nodeTotalTokens = n.Total,
                            spikeThreshold,
                            traceMedian = median,
                            absoluteThreshold = AbsoluteTokenSpikeThreshold,
                        },
                        message = $"Node {n.NodeId} consumed {n.Total} tokens — exceeds spike threshold ({spikeThreshold}).",
                    });
                }
            }
        }

        // logic_failure as anomaly (always — even if the saga recovered, the script error is
        // signal worth flagging).
        foreach (var eval in logicEvaluations.Where(e => !string.IsNullOrEmpty(e.FailureKind)))
        {
            anomalies.Add(new
            {
                kind = "logic_failure",
                severity = "high",
                nodeId = (Guid?)eval.NodeId,
                agentKey = (string?)null,
                evidence = new
                {
                    failureKind = eval.FailureKind,
                    failureMessage = AssistantToolJson.TruncateText(eval.FailureMessage, LongStringCap),
                },
                message = $"Logic evaluation at node {eval.NodeId} threw {eval.FailureKind}.",
            });
        }

        return anomalies;
    }

    private static object BuildTokenSummary(Dtos.TraceTokenUsageDto tokenAggregate)
    {
        if (tokenAggregate.ByNode.Count == 0)
        {
            return new
            {
                callCount = tokenAggregate.Total.CallCount,
                totalTokens = 0L,
                topNode = (object?)null,
            };
        }

        var ranked = tokenAggregate.ByNode
            .Select(n => new
            {
                n.NodeId,
                Total = n.Rollup.Totals.Values.Sum(),
            })
            .OrderByDescending(n => n.Total)
            .ToArray();

        var top = ranked[0];
        return new
        {
            callCount = tokenAggregate.Total.CallCount,
            totalTokens = tokenAggregate.Total.Totals.Values.Sum(),
            topNode = new
            {
                nodeId = top.NodeId,
                tokens = top.Total,
            },
        };
    }

    private static List<object> BuildSuggestions(
        WorkflowSagaStateEntity saga,
        IReadOnlyList<object> failingNodes,
        IReadOnlyList<object> anomalies)
    {
        var suggestions = new List<object>();
        var failed = string.Equals(saga.CurrentState, FailedState, StringComparison.Ordinal);

        if (failed)
        {
            // Replay-with-edit on the trace itself — the existing /traces/{id} inspector exposes
            // the replay flow; HAA-13 will eventually wire this into chat directly.
            suggestions.Add(new
            {
                kind = "replay_with_edit",
                target = $"/traces/{saga.TraceId}",
                message = "Open the trace inspector and use Replay-with-Edit to substitute a node's output and re-run from that point.",
            });
        }

        // For each failing node we know the agent of, propose a review of that agent.
        foreach (var node in failingNodes)
        {
            if (node.GetType().GetProperty("agentKey")?.GetValue(node) is string agentKey
                && !string.IsNullOrEmpty(agentKey))
            {
                suggestions.Add(new
                {
                    kind = "review_agent",
                    target = $"/agents/{agentKey}",
                    message = $"Review the '{agentKey}' agent's prompt template and system prompt — this is the agent that drove the failing decision.",
                });
                break; // one review_agent suggestion is enough; the LLM can mention others in prose
            }
        }

        if (anomalies.Any(a => a.GetType().GetProperty("kind")?.GetValue(a) as string == "token_spike"))
        {
            suggestions.Add(new
            {
                kind = "inspect_node_io",
                target = (string?)null,
                message = "Use get_node_io on the spike node to see whether the input grew unexpectedly large; consider trimming the upstream output via an inputScript or outputScript.",
            });
        }

        if (failed && string.IsNullOrEmpty(saga.FailureReason))
        {
            suggestions.Add(new
            {
                kind = "inspect_node_io",
                target = (string?)null,
                message = "No failure reason was recorded — call get_node_io on the last decision's node to see what the agent emitted before the implicit-Failed port routed.",
            });
        }

        return suggestions;
    }
}
