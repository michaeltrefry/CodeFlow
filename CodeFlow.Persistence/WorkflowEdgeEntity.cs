using CodeFlow.Runtime;

namespace CodeFlow.Persistence;

public sealed class WorkflowEdgeEntity
{
    public long Id { get; set; }

    public long WorkflowId { get; set; }

    public WorkflowEntity Workflow { get; set; } = null!;

    public string FromAgentKey { get; set; } = null!;

    public AgentDecisionKind Decision { get; set; }

    public string? DiscriminatorJson { get; set; }

    public string ToAgentKey { get; set; } = null!;

    public bool RotatesRound { get; set; }

    public int SortOrder { get; set; }
}
