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

    /// <summary>
    /// Author-acknowledged backedge: when true, the V6 backedge validator suppresses its
    /// "edge targets a node already reachable from its source" warning. Set via the editor's
    /// "Yes, intentional" override on a flagged edge.
    /// </summary>
    public bool IntentionalBackedge { get; set; }
}
