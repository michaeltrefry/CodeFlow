namespace CodeFlow.Persistence;

/// <summary>
/// A record of a single Logic-node evaluation during a workflow run — the script-host-emitted
/// counterpart to <see cref="DecisionRecord"/> for agents. Appended to
/// <see cref="WorkflowSagaStateEntity.LogicEvaluationHistoryJson"/>.
/// </summary>
public sealed record LogicEvaluationRecord(
    Guid NodeId,
    string? OutputPortName,
    Guid RoundId,
    TimeSpan Duration,
    IReadOnlyList<string> Logs,
    string? FailureKind,
    string? FailureMessage,
    DateTime RecordedAtUtc);
