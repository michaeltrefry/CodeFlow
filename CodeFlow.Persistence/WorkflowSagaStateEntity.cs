using CodeFlow.Runtime;
using MassTransit;
using System.Text.Json;

namespace CodeFlow.Persistence;

public sealed class WorkflowSagaStateEntity : SagaStateMachineInstance, ISagaVersion
{
    public Guid CorrelationId { get; set; }

    public Guid TraceId { get; set; }

    public string CurrentState { get; set; } = null!;

    public string CurrentAgentKey { get; set; } = null!;

    public Guid CurrentRoundId { get; set; }

    public int RoundCount { get; set; }

    public string AgentVersionsJson { get; set; } = "{}";

    public string DecisionHistoryJson { get; set; } = "[]";

    public string WorkflowKey { get; set; } = null!;

    public int WorkflowVersion { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public int Version { get; set; }

    /// <summary>
    /// When non-null, the saga has dispatched the workflow's escalation agent and is waiting for
    /// its completion. Holds the key of the agent whose overflowed edge triggered the escalation,
    /// so the workflow can resume from that point if the escalation agent approves recovery.
    /// Cleared after the escalation agent's decision is routed.
    /// </summary>
    public string? EscalatedFromAgentKey { get; set; }

    /// <summary>
    /// Transient routing flag set by the state machine during <see cref="AgentInvocationCompleted"/>
    /// handling so the conditional transition binders can select the terminal state. Not persisted.
    /// </summary>
    public string? PendingTransition { get; set; }

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

    public IReadOnlyList<DecisionRecord> GetDecisionHistory()
    {
        return WorkflowSagaJson.DeserializeDecisionHistory(DecisionHistoryJson);
    }

    public void AppendDecision(DecisionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var history = GetDecisionHistory().ToList();
        history.Add(record);
        DecisionHistoryJson = WorkflowSagaJson.SerializeDecisionHistory(history);
    }
}
