namespace CodeFlow.Persistence;

public sealed class WorkflowEntity
{
    public long Id { get; set; }

    public string Key { get; set; } = null!;

    public int Version { get; set; }

    public string Name { get; set; } = null!;

    public int MaxRoundsPerRound { get; set; }

    public WorkflowCategory Category { get; set; }

    /// <summary>
    /// Serialized JSON array of user-defined tag strings. Never null; empty array when no tags.
    /// </summary>
    public string TagsJson { get; set; } = "[]";

    public DateTime CreatedAtUtc { get; set; }

    public List<WorkflowNodeEntity> Nodes { get; set; } = [];

    public List<WorkflowEdgeEntity> Edges { get; set; } = [];

    public List<WorkflowInputEntity> Inputs { get; set; } = [];
}
