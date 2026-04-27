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
    IReadOnlyList<string>? Tags = null,
    // VZ2: optional declarations of which workflow variables this workflow expects to read
    // and write. Empty = no opt-in (workflow saves and runs identically to today). When
    // non-empty, the validator pipeline (WorkflowVarDeclarationRule) emits warnings if any
    // reachable agent reads / script writes a variable not in the corresponding list.
    IReadOnlyList<string>? WorkflowVarsReads = null,
    IReadOnlyList<string>? WorkflowVarsWrites = null)
{
    public IReadOnlyList<string> TagsOrEmpty => Tags ?? Array.Empty<string>();

    public IReadOnlyList<string> WorkflowVarsReadsOrEmpty =>
        WorkflowVarsReads ?? Array.Empty<string>();

    public IReadOnlyList<string> WorkflowVarsWritesOrEmpty =>
        WorkflowVarsWrites ?? Array.Empty<string>();

    public WorkflowNode StartNode =>
        Nodes.Single(node => node.Kind == WorkflowNodeKind.Start);

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

    /// <summary>
    /// Port names by which this workflow can exit. A terminal port is any declared
    /// <see cref="WorkflowNode.OutputPorts"/> entry whose <c>(nodeId, portName)</c> pair has no
    /// outgoing edge in <see cref="Edges"/>. Names are deduplicated across nodes. The implicit
    /// <c>Failed</c> port is intentionally excluded — it's an error sink, not a designed exit;
    /// authors who want a custom failure exit must wire <c>Failed</c> through to a different
    /// declared port. ReviewLoop nodes' synthesized <c>Exhausted</c> port is included when not
    /// wired downstream.
    /// </summary>
    public IReadOnlyCollection<string> TerminalPorts
    {
        get
        {
            var wired = new HashSet<(Guid NodeId, string Port)>();
            foreach (var edge in Edges)
            {
                wired.Add((edge.FromNodeId, edge.FromPort));
            }

            var terminals = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in Nodes)
            {
                foreach (var port in node.OutputPorts)
                {
                    if (string.IsNullOrWhiteSpace(port))
                    {
                        continue;
                    }
                    if (string.Equals(port, "Failed", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    if (!wired.Contains((node.Id, port)))
                    {
                        terminals.Add(port);
                    }
                }

                if (node.Kind == WorkflowNodeKind.ReviewLoop &&
                    !wired.Contains((node.Id, "Exhausted")))
                {
                    terminals.Add("Exhausted");
                }
            }

            return terminals;
        }
    }
}
