namespace CodeFlow.Persistence;

public sealed class WorkflowEdgeEntity
{
    public long Id { get; set; }

    public long WorkflowId { get; set; }

    public WorkflowEntity Workflow { get; set; } = null!;

    public Guid FromNodeId { get; set; }

    public string FromPort { get; set; } = null!;

    public Guid ToNodeId { get; set; }

    public string ToPort { get; set; } = null!;

    public bool RotatesRound { get; set; }

    public int SortOrder { get; set; }
}
