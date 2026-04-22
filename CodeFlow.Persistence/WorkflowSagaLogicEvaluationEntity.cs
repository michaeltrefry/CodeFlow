namespace CodeFlow.Persistence;

public sealed class WorkflowSagaLogicEvaluationEntity
{
    public Guid SagaCorrelationId { get; set; }

    public int Ordinal { get; set; }

    public Guid TraceId { get; set; }

    public Guid NodeId { get; set; }

    public string? OutputPortName { get; set; }

    public Guid RoundId { get; set; }

    public long DurationTicks { get; set; }

    public string LogsJson { get; set; } = "[]";

    public string? FailureKind { get; set; }

    public string? FailureMessage { get; set; }

    public DateTime RecordedAtUtc { get; set; }
}
