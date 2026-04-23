namespace CodeFlow.Persistence;

public sealed class WorkflowNodeEntity
{
    public long Id { get; set; }

    public long WorkflowId { get; set; }

    public WorkflowEntity Workflow { get; set; } = null!;

    public Guid NodeId { get; set; }

    public WorkflowNodeKind Kind { get; set; }

    public string? AgentKey { get; set; }

    public int? AgentVersion { get; set; }

    public string? Script { get; set; }

    public string OutputPortsJson { get; set; } = "[]";

    public double LayoutX { get; set; }

    public double LayoutY { get; set; }

    public string? SubflowKey { get; set; }

    public int? SubflowVersion { get; set; }

    public int? ReviewMaxRounds { get; set; }

    public string? LoopDecision { get; set; }
}
