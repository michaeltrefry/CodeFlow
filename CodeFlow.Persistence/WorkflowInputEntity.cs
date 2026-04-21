namespace CodeFlow.Persistence;

public sealed class WorkflowInputEntity
{
    public long Id { get; set; }

    public long WorkflowId { get; set; }

    public WorkflowEntity Workflow { get; set; } = null!;

    public string Key { get; set; } = null!;

    public string DisplayName { get; set; } = null!;

    public WorkflowInputKind Kind { get; set; }

    public bool Required { get; set; }

    public string? DefaultValueJson { get; set; }

    public string? Description { get; set; }

    public int Ordinal { get; set; }
}
