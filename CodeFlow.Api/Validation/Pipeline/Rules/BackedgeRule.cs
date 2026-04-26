using CodeFlow.Api.Dtos;

namespace CodeFlow.Api.Validation.Pipeline.Rules;

/// <summary>
/// V6: Flag DFS-tree backedges in the authored edge graph — edges that close a cycle by
/// pointing back to an ancestor. Soft-warning: the author can dismiss with "Yes, intentional"
/// by setting the edge's <see cref="WorkflowEdgeDto.IntentionalBackedge"/> flag, after which
/// subsequent saves do not re-emit the warning.
///
/// Standard DFS-coloring algorithm: nodes are White (unseen) / Gray (on the active DFS stack) /
/// Black (fully explored). An edge (u, v) is a backedge iff v is Gray when we walk it. Roots
/// are picked by topology — nodes with no incoming edges first, then any leftover Whites — so
/// the "back" edge is the loopback the author would naturally dismiss, not the forward edge
/// that started the cycle.
///
/// ReviewLoop iteration is internal to the loop primitive and not represented as an authored
/// edge, so it never trips this check.
/// </summary>
public sealed class BackedgeRule : IWorkflowValidationRule
{
    public string RuleId => "backedge";

    public int Order => 220;

    public Task<IReadOnlyList<WorkflowValidationFinding>> RunAsync(
        WorkflowValidationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Edges.Count == 0 || context.Nodes.Count == 0)
        {
            return EmptyTask;
        }

        // Adjacency keyed on source node — values are the outgoing edges (preserve duplicates so
        // we can locate the offending DTO when emitting a finding).
        var outgoing = new Dictionary<Guid, List<WorkflowEdgeDto>>();
        var incomingCount = new Dictionary<Guid, int>();
        foreach (var node in context.Nodes)
        {
            outgoing[node.Id] = new List<WorkflowEdgeDto>();
            incomingCount[node.Id] = 0;
        }
        foreach (var edge in context.Edges)
        {
            if (!outgoing.TryGetValue(edge.FromNodeId, out var list))
            {
                list = new List<WorkflowEdgeDto>();
                outgoing[edge.FromNodeId] = list;
            }
            list.Add(edge);
            incomingCount[edge.ToNodeId] = incomingCount.GetValueOrDefault(edge.ToNodeId) + 1;
        }

        var color = new Dictionary<Guid, NodeColor>();
        foreach (var nodeId in outgoing.Keys)
        {
            color[nodeId] = NodeColor.White;
        }

        var backedges = new List<WorkflowEdgeDto>();

        // Roots first: nodes with no incoming edges (typically the Start node and any
        // disconnected entries). Then sweep any leftover Whites in case the graph has a cycle
        // with no entry point (every node has at least one incoming edge).
        var rootOrder = context.Nodes
            .Where(n => incomingCount.GetValueOrDefault(n.Id) == 0)
            .Select(n => n.Id)
            .Concat(context.Nodes.Select(n => n.Id))
            .Distinct()
            .ToArray();

        foreach (var root in rootOrder)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (color[root] != NodeColor.White)
            {
                continue;
            }
            DepthFirstSearch(root, outgoing, color, backedges, cancellationToken);
        }

        var findings = new List<WorkflowValidationFinding>();
        var seenAnchors = new HashSet<(Guid From, string FromPort, Guid To)>();

        foreach (var edge in backedges)
        {
            if (edge.IntentionalBackedge)
            {
                continue;
            }

            var anchor = (edge.FromNodeId, edge.FromPort ?? string.Empty, edge.ToNodeId);
            if (!seenAnchors.Add(anchor))
            {
                continue;
            }

            findings.Add(new WorkflowValidationFinding(
                RuleId: RuleId,
                Severity: WorkflowValidationSeverity.Warning,
                Message: $"Edge from port '{edge.FromPort}' targets a node that is already an "
                    + "ancestor — a cycle. ReviewLoop iteration handles loops natively, so this "
                    + "is usually a mistake. If the cycle is deliberate, set the edge's "
                    + "intentionalBackedge flag to dismiss this warning.",
                Location: new WorkflowValidationLocation(
                    EdgeFrom: edge.FromNodeId,
                    EdgePort: edge.FromPort)));
        }

        return Task.FromResult<IReadOnlyList<WorkflowValidationFinding>>(findings);
    }

    private static void DepthFirstSearch(
        Guid root,
        IReadOnlyDictionary<Guid, List<WorkflowEdgeDto>> outgoing,
        Dictionary<Guid, NodeColor> color,
        List<WorkflowEdgeDto> backedges,
        CancellationToken cancellationToken)
    {
        // Iterative DFS to avoid stack overflows on pathological depth (and to keep cancellation
        // checks responsive). The frame tracks where we are in the iteration over a node's
        // outgoing edges; on recursion we push a new frame, on completion we mark Black and pop.
        var stack = new Stack<DfsFrame>();
        color[root] = NodeColor.Gray;
        stack.Push(new DfsFrame(root, 0));

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var frame = stack.Peek();
            var edges = outgoing.TryGetValue(frame.NodeId, out var list) ? list : null;

            if (edges is null || frame.NextEdgeIndex >= edges.Count)
            {
                color[frame.NodeId] = NodeColor.Black;
                stack.Pop();
                continue;
            }

            var edge = edges[frame.NextEdgeIndex];
            stack.Pop();
            stack.Push(frame with { NextEdgeIndex = frame.NextEdgeIndex + 1 });

            if (!color.TryGetValue(edge.ToNodeId, out var targetColor))
            {
                // Target isn't a known node (orphan reference) — treat as White and skip.
                continue;
            }

            switch (targetColor)
            {
                case NodeColor.White:
                    color[edge.ToNodeId] = NodeColor.Gray;
                    stack.Push(new DfsFrame(edge.ToNodeId, 0));
                    break;
                case NodeColor.Gray:
                    // Edge points to an ancestor on the active DFS path — backedge.
                    backedges.Add(edge);
                    break;
                case NodeColor.Black:
                    // Cross / forward edge to an already-finished subtree: not a cycle.
                    break;
            }
        }
    }

    private enum NodeColor
    {
        White,
        Gray,
        Black,
    }

    private sealed record DfsFrame(Guid NodeId, int NextEdgeIndex);

    private static readonly Task<IReadOnlyList<WorkflowValidationFinding>> EmptyTask =
        Task.FromResult<IReadOnlyList<WorkflowValidationFinding>>(Array.Empty<WorkflowValidationFinding>());
}
