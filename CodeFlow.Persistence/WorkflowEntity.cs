namespace CodeFlow.Persistence;

public sealed class WorkflowEntity
{
    public long Id { get; set; }

    public string Key { get; set; } = null!;

    public int Version { get; set; }

    public string Name { get; set; } = null!;

    public string StartAgentKey { get; set; } = null!;

    public string? EscalationAgentKey { get; set; }

    public int MaxRoundsPerRound { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public List<WorkflowEdgeEntity> Edges { get; set; } = [];
}
