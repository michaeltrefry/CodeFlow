namespace CodeFlow.Persistence;

public sealed class HitlTaskEntity
{
    public long Id { get; set; }

    public Guid TraceId { get; set; }

    public Guid RoundId { get; set; }

    public Guid NodeId { get; set; }

    public string AgentKey { get; set; } = null!;

    public int AgentVersion { get; set; }

    public string WorkflowKey { get; set; } = null!;

    public int WorkflowVersion { get; set; }

    public string InputRef { get; set; } = null!;

    public string? InputPreview { get; set; }

    public HitlTaskState State { get; set; }

    public string? Decision { get; set; }

    public string? DecisionPayloadJson { get; set; }

    public string? DeciderId { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? DecidedAtUtc { get; set; }
}

public enum HitlTaskState
{
    Pending = 0,
    Decided = 1,
    Cancelled = 2
}
