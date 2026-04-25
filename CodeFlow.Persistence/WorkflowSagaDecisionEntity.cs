namespace CodeFlow.Persistence;

public sealed class WorkflowSagaDecisionEntity
{
    public Guid SagaCorrelationId { get; set; }

    public int Ordinal { get; set; }

    public Guid TraceId { get; set; }

    public string AgentKey { get; set; } = string.Empty;

    public int AgentVersion { get; set; }

    public string Decision { get; set; } = string.Empty;

    public string? DecisionPayloadJson { get; set; }

    public Guid RoundId { get; set; }

    public DateTime RecordedAtUtc { get; set; }

    public Guid? NodeId { get; set; }

    public string? OutputPortName { get; set; }

    public string? InputRef { get; set; }

    public string? OutputRef { get; set; }
}
