using CodeFlow.Persistence;

namespace CodeFlow.Orchestration.Replay;

public enum DriftLevel
{
    None = 0,
    Soft = 1,
    Hard = 2,
}

public sealed record DriftReport(
    DriftLevel Level,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Compares the workflow definition the saga was pinned to against the workflow definition the
/// replay will run against. Hard drift refuses the replay (callers can opt back in with
/// <c>force=true</c>); Soft drift surfaces warnings but still runs; None means the structural shape
/// has not moved.
///
/// "Structurally equivalent" means: the set of node identities, kinds, and agent-key/version pins
/// match, and every edge connects the same <c>(fromNodeId, fromPort) → (toNodeId, toPort)</c>.
/// Layout, sort order, and on-node script text are intentionally ignored — they don't affect the
/// dry-run trajectory and would otherwise inflate Soft drift on every cosmetic edit.
/// </summary>
public static class ReplayDriftDetector
{
    public static DriftReport Detect(
        Workflow originalWorkflow,
        IReadOnlyDictionary<string, int> pinnedAgentVersions,
        Workflow targetWorkflow,
        IReadOnlyList<RecordedDecisionRef> recordedDecisions)
    {
        ArgumentNullException.ThrowIfNull(originalWorkflow);
        ArgumentNullException.ThrowIfNull(pinnedAgentVersions);
        ArgumentNullException.ThrowIfNull(targetWorkflow);
        ArgumentNullException.ThrowIfNull(recordedDecisions);

        var hardWarnings = new List<string>();
        var softWarnings = new List<string>();

        var targetNodesById = targetWorkflow.Nodes.ToDictionary(n => n.Id);
        var originalNodesById = originalWorkflow.Nodes.ToDictionary(n => n.Id);

        // Hard: any node referenced by a recorded decision must still exist with the same Kind and
        // (when the original was an Agent/HITL) the same agent key. Renaming an agent on a node is
        // fine — pinned-version drift is a separate check below.
        var seenNodeIds = new HashSet<Guid>();
        foreach (var decision in recordedDecisions)
        {
            if (decision.NodeId is not Guid nodeId)
            {
                continue;
            }

            if (!seenNodeIds.Add(nodeId))
            {
                continue;
            }

            if (!targetNodesById.TryGetValue(nodeId, out var targetNode))
            {
                hardWarnings.Add(
                    $"Node {nodeId} was referenced by recorded decisions but is not present in target workflow "
                    + $"'{targetWorkflow.Key}' v{targetWorkflow.Version}.");
                continue;
            }

            if (originalNodesById.TryGetValue(nodeId, out var originalNode))
            {
                if (originalNode.Kind != targetNode.Kind)
                {
                    hardWarnings.Add(
                        $"Node {nodeId} changed kind: {originalNode.Kind} → {targetNode.Kind}.");
                }

                if (!string.Equals(originalNode.AgentKey, targetNode.AgentKey, StringComparison.Ordinal))
                {
                    hardWarnings.Add(
                        $"Node {nodeId} changed agent key: '{originalNode.AgentKey}' → '{targetNode.AgentKey}'.");
                }
            }
        }

        // Hard: every agent the saga pinned must still exist in the target workflow at a version
        // ≥ what was pinned (the agent itself may have been bumped, that's Soft, but a removed
        // agent is Hard).
        var targetAgents = targetWorkflow.Nodes
            .Where(n => n.Kind == WorkflowNodeKind.Agent || n.Kind == WorkflowNodeKind.Hitl)
            .Where(n => !string.IsNullOrEmpty(n.AgentKey))
            .Select(n => (Key: n.AgentKey!, Version: n.AgentVersion ?? 0))
            .ToLookup(x => x.Key, StringComparer.Ordinal);

        foreach (var (pinnedAgentKey, pinnedVersion) in pinnedAgentVersions)
        {
            // Skip pins for agents that aren't actually used in the original workflow's reachable
            // nodes — old saga rows may carry historical pins for agents that have since been
            // removed from the workflow but were never invoked in this trace.
            var originalUsed = originalWorkflow.Nodes.Any(n =>
                string.Equals(n.AgentKey, pinnedAgentKey, StringComparison.Ordinal));
            if (!originalUsed)
            {
                continue;
            }

            if (!targetAgents.Contains(pinnedAgentKey))
            {
                hardWarnings.Add(
                    $"Agent '{pinnedAgentKey}' was pinned at version {pinnedVersion} but is no longer used "
                    + $"in target workflow '{targetWorkflow.Key}' v{targetWorkflow.Version}.");
                continue;
            }

            var targetVersionForAgent = targetAgents[pinnedAgentKey].Max(x => x.Version);
            if (targetVersionForAgent != pinnedVersion)
            {
                softWarnings.Add(
                    $"Agent '{pinnedAgentKey}' moved from pinned version {pinnedVersion} to "
                    + $"{targetVersionForAgent} in target workflow.");
            }
        }

        // Soft: any structural difference between original and target (when they're different
        // versions). We compare node-id/kind/agent-key sets and the edge tuple set. Layout and
        // sort-order are intentionally not in the equivalence relation.
        if (!StructurallyEquivalent(originalWorkflow, targetWorkflow, out var equivalenceWarnings))
        {
            softWarnings.AddRange(equivalenceWarnings);
        }

        if (hardWarnings.Count > 0)
        {
            return new DriftReport(DriftLevel.Hard, hardWarnings.Concat(softWarnings).ToArray());
        }

        if (softWarnings.Count > 0)
        {
            return new DriftReport(DriftLevel.Soft, softWarnings);
        }

        return new DriftReport(DriftLevel.None, Array.Empty<string>());
    }

    /// <summary>
    /// Schema-stable equivalence: same node identities + kinds + agent-key/version pins, and the
    /// same edge tuple set <c>(fromNodeId, fromPort, toNodeId, toPort)</c>. Layout and sort-order
    /// are intentionally ignored.
    /// </summary>
    public static bool StructurallyEquivalent(
        Workflow a,
        Workflow b,
        out IReadOnlyList<string> differences)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        var diffs = new List<string>();

        var aNodes = a.Nodes.ToDictionary(n => n.Id);
        var bNodes = b.Nodes.ToDictionary(n => n.Id);

        foreach (var aId in aNodes.Keys)
        {
            if (!bNodes.ContainsKey(aId))
            {
                diffs.Add($"Node {aId} was removed.");
            }
        }

        foreach (var bId in bNodes.Keys)
        {
            if (!aNodes.ContainsKey(bId))
            {
                diffs.Add($"Node {bId} was added.");
            }
        }

        foreach (var (id, aNode) in aNodes)
        {
            if (!bNodes.TryGetValue(id, out var bNode))
            {
                continue;
            }

            if (aNode.Kind != bNode.Kind)
            {
                diffs.Add($"Node {id} kind {aNode.Kind} → {bNode.Kind}.");
            }

            if (!string.Equals(aNode.AgentKey, bNode.AgentKey, StringComparison.Ordinal))
            {
                diffs.Add($"Node {id} agent key '{aNode.AgentKey}' → '{bNode.AgentKey}'.");
            }

            if (aNode.AgentVersion != bNode.AgentVersion)
            {
                diffs.Add($"Node {id} agent version {aNode.AgentVersion} → {bNode.AgentVersion}.");
            }
        }

        var aEdges = a.Edges
            .Select(e => (e.FromNodeId, e.FromPort, e.ToNodeId, e.ToPort))
            .ToHashSet();
        var bEdges = b.Edges
            .Select(e => (e.FromNodeId, e.FromPort, e.ToNodeId, e.ToPort))
            .ToHashSet();

        foreach (var edge in aEdges)
        {
            if (!bEdges.Contains(edge))
            {
                diffs.Add($"Edge {edge.FromNodeId}.{edge.FromPort} → {edge.ToNodeId}.{edge.ToPort} was removed.");
            }
        }

        foreach (var edge in bEdges)
        {
            if (!aEdges.Contains(edge))
            {
                diffs.Add($"Edge {edge.FromNodeId}.{edge.FromPort} → {edge.ToNodeId}.{edge.ToPort} was added.");
            }
        }

        differences = diffs;
        return diffs.Count == 0;
    }
}
