using CodeFlow.Runtime;
using MassTransit;
using System.Text.Json;

namespace CodeFlow.Persistence;

public sealed class WorkflowSagaStateEntity : SagaStateMachineInstance, ISagaVersion
{
    public Guid CorrelationId { get; set; }

    public Guid TraceId { get; set; }

    public string CurrentState { get; set; } = null!;

    public Guid CurrentNodeId { get; set; }

    public string CurrentAgentKey { get; set; } = string.Empty;

    public Guid CurrentRoundId { get; set; }

    public int RoundCount { get; set; }

    public string AgentVersionsJson { get; set; } = "{}";

    // Legacy JSON history columns. No longer written or read after F-012 — superseded by the
    // workflow_saga_decisions / workflow_saga_logic_evaluations child tables. Retained on the
    // row so existing sagas keep their history on disk until a follow-up migration drops the
    // columns once all in-flight sagas have drained.
    public string DecisionHistoryJson { get; set; } = "[]";

    public string LogicEvaluationHistoryJson { get; set; } = "[]";

    public int DecisionCount { get; set; }

    public int LogicEvaluationCount { get; set; }

    public string WorkflowKey { get; set; } = null!;

    public int WorkflowVersion { get; set; }

    public string InputsJson { get; set; } = "{}";

    /// <summary>
    /// Input artifact URI that was sent to the currently-active agent. Tracked so the saga can
    /// record it on the next decision and so the trace detail endpoint can surface the pairing
    /// of input → output per agent invocation.
    /// </summary>
    public string? CurrentInputRef { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public int Version { get; set; }

    /// <summary>
    /// When non-null, the saga has dispatched the workflow's escalation node and is waiting for
    /// its completion. Holds the node id of the source whose overflowed edge triggered escalation,
    /// so the workflow can resume from that point if the escalation agent approves recovery.
    /// Cleared after the escalation decision is routed.
    /// </summary>
    public Guid? EscalatedFromNodeId { get; set; }

    /// <summary>
    /// Set when the saga transitions to a terminal failure state. Describes why routing could not
    /// continue (e.g. missing edge, logic configuration error, max-rounds exceeded) so operators
    /// can diagnose workflow issues without reading raw event logs.
    /// </summary>
    public string? FailureReason { get; set; }

    /// <summary>
    /// When set, this saga is a child invocation spawned by a Subflow node on another saga.
    /// Identifies the parent saga's trace, the Subflow node id, and the parent's round at the
    /// moment of dispatch — used to publish <c>SubflowCompleted</c> back to the parent and to
    /// surface descendant HITL on the parent trace. Null for top-level sagas.
    /// </summary>
    public Guid? ParentTraceId { get; set; }

    public Guid? ParentNodeId { get; set; }

    public Guid? ParentRoundId { get; set; }

    /// <summary>
    /// Subflow nesting depth: 0 for top-level sagas; +1 per nested Subflow invocation. Capped at
    /// the configured maximum (3) to prevent runaway recursion.
    /// </summary>
    public int SubflowDepth { get; set; }

    /// <summary>
    /// JSON-serialized "global" context bag shared with descendant subflows. Treated as <c>{}</c>
    /// when null. Distinct from <see cref="InputsJson"/>, which holds the local context exposed
    /// to scripts as <c>context</c>; <c>global</c> is exposed as a separate object and may be
    /// mutated by <c>setGlobal</c> in scripts. On subflow completion the child's final global is
    /// shallow-merged into the parent's global before routing.
    /// </summary>
    public string? GlobalInputsJson { get; set; }

    /// <summary>
    /// Set when this saga is a ReviewLoop iteration: 1-indexed round number within the parent's
    /// ReviewLoop node. Null for plain subflow invocations and top-level sagas. Used by script/
    /// template binding to expose <c>round</c> and <c>isLastRound</c> to the child workflow.
    /// </summary>
    public int? ParentReviewRound { get; set; }

    /// <summary>
    /// Snapshot of the parent ReviewLoop node's <c>MaxRounds</c> setting at the moment the child
    /// was spawned, so the child can compute <c>isLastRound</c> without re-reading the parent
    /// workflow. Null for non-ReviewLoop sagas.
    /// </summary>
    public int? ParentReviewMaxRounds { get; set; }

    /// <summary>
    /// The effective terminal port of the last-routed source node — i.e. the port the saga
    /// actually picked when dispatching the next hop. When the source node had a routing
    /// script, this is the script's <c>setNodePath(...)</c> choice; otherwise it equals the
    /// decision-kind-derived port. Persisted so <see cref="SubflowCompleted.TerminalPort"/>
    /// can be populated on terminal, which a ReviewLoop parent compares against its
    /// configured <c>LoopDecision</c> to decide whether to iterate.
    /// </summary>
    public string? LastEffectivePort { get; set; }

    /// <summary>
    /// Propagated from the parent ReviewLoop node's <c>LoopDecision</c> setting at spawn time.
    /// When an unwired port on a child saga matches this value (case-sensitive), the port is
    /// treated as a legal clean exit — the child saga terminates <c>Completed</c> and the
    /// effective port rides up on <see cref="SubflowCompleted.TerminalPort"/> so the parent
    /// can route accordingly. Null for plain Subflow children and top-level sagas; the
    /// existing Completed/Approved/Rejected clean-exit allowlist still applies in those
    /// cases.
    /// </summary>
    public string? ParentLoopDecision { get; set; }

    /// <summary>
    /// Transient routing flag set by the state machine during <see cref="AgentInvocationCompleted"/>
    /// handling so the conditional transition binders can select the terminal state. Not persisted.
    /// </summary>
    public string? PendingTransition { get; set; }

    public ICollection<WorkflowSagaDecisionEntity> Decisions { get; set; } = new List<WorkflowSagaDecisionEntity>();

    public ICollection<WorkflowSagaLogicEvaluationEntity> LogicEvaluations { get; set; } = new List<WorkflowSagaLogicEvaluationEntity>();

    public IReadOnlyDictionary<string, int> GetPinnedAgentVersions()
    {
        return WorkflowSagaJson.DeserializePinnedVersions(AgentVersionsJson);
    }

    public void SetPinnedAgentVersions(IReadOnlyDictionary<string, int> versions)
    {
        AgentVersionsJson = WorkflowSagaJson.SerializePinnedVersions(versions);
    }

    public int? GetPinnedVersion(string agentKey)
    {
        var versions = GetPinnedAgentVersions();
        return versions.TryGetValue(agentKey, out var version) ? version : null;
    }

    public void PinAgentVersion(string agentKey, int version)
    {
        var versions = new Dictionary<string, int>(GetPinnedAgentVersions(), StringComparer.Ordinal)
        {
            [agentKey] = version
        };

        SetPinnedAgentVersions(versions);
    }

    /// <summary>
    /// Returns the decision history from the tracked <see cref="Decisions"/> navigation collection.
    /// For DB-backed reads to see all persisted history, the caller must eager-load the navigation
    /// (e.g. <c>.Include(s =&gt; s.Decisions)</c>) or query <c>WorkflowSagaDecisions</c> directly.
    /// </summary>
    public IReadOnlyList<DecisionRecord> GetDecisionHistory()
    {
        return Decisions
            .OrderBy(d => d.Ordinal)
            .Select(DecisionEntityMapping.ToRecord)
            .ToArray();
    }

    public void AppendDecision(DecisionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        Decisions.Add(DecisionEntityMapping.ToEntity(CorrelationId, TraceId, DecisionCount, record));
        DecisionCount += 1;
    }

    /// <summary>
    /// Returns the logic evaluation history from the tracked <see cref="LogicEvaluations"/>
    /// navigation collection. Same loading caveat as <see cref="GetDecisionHistory"/>.
    /// </summary>
    public IReadOnlyList<LogicEvaluationRecord> GetLogicEvaluationHistory()
    {
        return LogicEvaluations
            .OrderBy(e => e.Ordinal)
            .Select(LogicEvaluationEntityMapping.ToRecord)
            .ToArray();
    }

    public void AppendLogicEvaluation(LogicEvaluationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        LogicEvaluations.Add(LogicEvaluationEntityMapping.ToEntity(
            CorrelationId,
            TraceId,
            LogicEvaluationCount,
            record));
        LogicEvaluationCount += 1;
    }
}

internal static class DecisionEntityMapping
{
    public static WorkflowSagaDecisionEntity ToEntity(
        Guid correlationId,
        Guid traceId,
        int ordinal,
        DecisionRecord record)
    {
        return new WorkflowSagaDecisionEntity
        {
            SagaCorrelationId = correlationId,
            Ordinal = ordinal,
            TraceId = traceId,
            AgentKey = record.AgentKey,
            AgentVersion = record.AgentVersion,
            Decision = record.Decision,
            DecisionPayloadJson = record.DecisionPayload?.GetRawText(),
            RoundId = record.RoundId,
            RecordedAtUtc = record.RecordedAtUtc,
            NodeId = record.NodeId,
            OutputPortName = record.OutputPortName,
            InputRef = record.InputRef,
            OutputRef = record.OutputRef
        };
    }

    public static DecisionRecord ToRecord(WorkflowSagaDecisionEntity entity)
    {
        JsonElement? payload = null;
        if (!string.IsNullOrWhiteSpace(entity.DecisionPayloadJson))
        {
            using var document = JsonDocument.Parse(entity.DecisionPayloadJson);
            payload = document.RootElement.Clone();
        }

        return new DecisionRecord(
            AgentKey: entity.AgentKey,
            AgentVersion: entity.AgentVersion,
            Decision: entity.Decision,
            DecisionPayload: payload,
            RoundId: entity.RoundId,
            RecordedAtUtc: entity.RecordedAtUtc,
            NodeId: entity.NodeId,
            OutputPortName: entity.OutputPortName,
            InputRef: entity.InputRef,
            OutputRef: entity.OutputRef);
    }
}

internal static class LogicEvaluationEntityMapping
{
    private static readonly JsonSerializerOptions LogsSerializerOptions = new(JsonSerializerDefaults.Web);

    public static WorkflowSagaLogicEvaluationEntity ToEntity(
        Guid correlationId,
        Guid traceId,
        int ordinal,
        LogicEvaluationRecord record)
    {
        return new WorkflowSagaLogicEvaluationEntity
        {
            SagaCorrelationId = correlationId,
            Ordinal = ordinal,
            TraceId = traceId,
            NodeId = record.NodeId,
            OutputPortName = record.OutputPortName,
            RoundId = record.RoundId,
            DurationTicks = record.Duration.Ticks,
            LogsJson = JsonSerializer.Serialize(record.Logs, LogsSerializerOptions),
            FailureKind = record.FailureKind,
            FailureMessage = record.FailureMessage,
            RecordedAtUtc = record.RecordedAtUtc
        };
    }

    public static LogicEvaluationRecord ToRecord(WorkflowSagaLogicEvaluationEntity entity)
    {
        var logs = string.IsNullOrWhiteSpace(entity.LogsJson)
            ? Array.Empty<string>()
            : JsonSerializer.Deserialize<string[]>(entity.LogsJson, LogsSerializerOptions) ?? Array.Empty<string>();

        return new LogicEvaluationRecord(
            NodeId: entity.NodeId,
            OutputPortName: entity.OutputPortName,
            RoundId: entity.RoundId,
            Duration: TimeSpan.FromTicks(entity.DurationTicks),
            Logs: logs,
            FailureKind: entity.FailureKind,
            FailureMessage: entity.FailureMessage,
            RecordedAtUtc: entity.RecordedAtUtc);
    }
}
