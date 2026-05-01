namespace CodeFlow.Persistence;

public sealed class WorkflowEntity
{
    public long Id { get; set; }

    public string Key { get; set; } = null!;

    public int Version { get; set; }

    public string Name { get; set; } = null!;

    public int MaxRoundsPerRound { get; set; }

    public WorkflowCategory Category { get; set; }

    public bool IsRetired { get; set; }

    /// <summary>
    /// Serialized JSON array of user-defined tag strings. Never null; empty array when no tags.
    /// </summary>
    public string TagsJson { get; set; } = "[]";

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>
    /// VZ2: nullable JSON array of workflow-variable keys this workflow declares it reads
    /// (and expects upstream nodes to write before they're consumed). NULL = no declaration,
    /// validator skips. Empty array = author declared "reads nothing" explicitly.
    /// </summary>
    public string? WorkflowVarsReadsJson { get; set; }

    /// <summary>
    /// VZ2: nullable JSON array of workflow-variable keys this workflow's nodes are allowed
    /// to write via <c>setWorkflow</c> / mirror / rejection-history. Same NULL vs empty
    /// distinction as <see cref="WorkflowVarsReadsJson"/>.
    /// </summary>
    public string? WorkflowVarsWritesJson { get; set; }

    public List<WorkflowNodeEntity> Nodes { get; set; } = [];

    public List<WorkflowEdgeEntity> Edges { get; set; } = [];

    public List<WorkflowInputEntity> Inputs { get; set; } = [];
}
