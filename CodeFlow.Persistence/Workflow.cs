namespace CodeFlow.Persistence;

public sealed record Workflow(
    string Key,
    int Version,
    string Name,
    int MaxRoundsPerRound,
    DateTime CreatedAtUtc,
    IReadOnlyList<WorkflowNode> Nodes,
    IReadOnlyList<WorkflowEdge> Edges,
    IReadOnlyList<WorkflowInput> Inputs,
    WorkflowCategory Category = WorkflowCategory.Workflow,
    IReadOnlyList<string>? Tags = null)
{
    public IReadOnlyList<string> TagsOrEmpty => Tags ?? Array.Empty<string>();

    public WorkflowNode StartNode =>
        Nodes.Single(node => node.Kind == WorkflowNodeKind.Start);

    public WorkflowNode? EscalationNode =>
        Nodes.SingleOrDefault(node => node.Kind == WorkflowNodeKind.Escalation);

    public WorkflowNode? FindNode(Guid nodeId) =>
        Nodes.FirstOrDefault(node => node.Id == nodeId);

    public WorkflowEdge? FindNext(Guid fromNodeId, string outputPortName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPortName);

        return Edges
            .Where(edge =>
                edge.FromNodeId == fromNodeId &&
                string.Equals(edge.FromPort, outputPortName, StringComparison.Ordinal))
            .OrderBy(edge => edge.SortOrder)
            .FirstOrDefault();
    }
}
