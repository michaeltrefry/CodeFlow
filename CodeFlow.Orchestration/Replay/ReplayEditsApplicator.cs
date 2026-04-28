using CodeFlow.Orchestration.DryRun;
using CodeFlow.Persistence;

namespace CodeFlow.Orchestration.Replay;

/// <summary>
/// Applies the request body's edits + additionalMocks on top of the per-agent queues produced by
/// <see cref="ReplayMockExtractor"/>. Validation runs against a pre-built
/// <c>agent → declared output ports</c> index so the caller can include subflow bodies (the root
/// workflow alone doesn't see agents declared inside Subflow/ReviewLoop subtrees).
/// </summary>
public static class ReplayEditsApplicator
{
    public sealed record ApplyResult(
        IReadOnlyDictionary<string, IReadOnlyList<DryRunMockResponse>> Mocks,
        IReadOnlyList<string> ValidationErrors);

    public static ApplyResult Apply(
        IReadOnlyDictionary<string, IReadOnlyList<DryRunMockResponse>> baseMocks,
        IReadOnlyList<ReplayEdit>? edits,
        IReadOnlyDictionary<string, IReadOnlyList<DryRunMockResponse>>? additionalMocks,
        IReadOnlyDictionary<string, IReadOnlySet<string>> declaredPortsByAgent,
        string targetWorkflowDisplayLabel)
    {
        ArgumentNullException.ThrowIfNull(baseMocks);
        ArgumentNullException.ThrowIfNull(declaredPortsByAgent);
        ArgumentNullException.ThrowIfNull(targetWorkflowDisplayLabel);

        var errors = new List<string>();

        var working = new Dictionary<string, List<DryRunMockResponse>>(StringComparer.Ordinal);
        foreach (var (agentKey, queue) in baseMocks)
        {
            working[agentKey] = new List<DryRunMockResponse>(queue);
        }

        if (edits is not null)
        {
            for (var i = 0; i < edits.Count; i++)
            {
                var edit = edits[i];
                if (edit is null)
                {
                    errors.Add($"edits[{i}]: edit is null.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(edit.AgentKey))
                {
                    errors.Add($"edits[{i}]: agentKey is required.");
                    continue;
                }

                if (!working.TryGetValue(edit.AgentKey, out var queue) || queue.Count == 0)
                {
                    errors.Add($"edits[{i}]: agent '{edit.AgentKey}' has no recorded decisions to edit.");
                    continue;
                }

                if (edit.Ordinal < 1 || edit.Ordinal > queue.Count)
                {
                    errors.Add(
                        $"edits[{i}]: ordinal {edit.Ordinal} is out of range for agent '{edit.AgentKey}' "
                        + $"(recorded responses: {queue.Count}).");
                    continue;
                }

                if (!string.IsNullOrEmpty(edit.Decision)
                    && !AgentDeclaresPort(declaredPortsByAgent, edit.AgentKey, edit.Decision))
                {
                    errors.Add(
                        $"edits[{i}]: decision '{edit.Decision}' is not declared as an output port "
                        + $"for agent '{edit.AgentKey}' in workflow {targetWorkflowDisplayLabel}.");
                    continue;
                }

                var existing = queue[edit.Ordinal - 1];
                queue[edit.Ordinal - 1] = new DryRunMockResponse(
                    Decision: string.IsNullOrEmpty(edit.Decision) ? existing.Decision : edit.Decision!,
                    Output: edit.Output ?? existing.Output,
                    Payload: edit.Payload ?? existing.Payload);
            }
        }

        if (additionalMocks is not null)
        {
            foreach (var (agentKey, additions) in additionalMocks)
            {
                if (string.IsNullOrWhiteSpace(agentKey) || additions is null || additions.Count == 0)
                {
                    continue;
                }

                if (!working.TryGetValue(agentKey, out var queue))
                {
                    queue = new List<DryRunMockResponse>();
                    working[agentKey] = queue;
                }

                for (var j = 0; j < additions.Count; j++)
                {
                    var addition = additions[j];
                    if (addition is null)
                    {
                        continue;
                    }

                    if (!AgentDeclaresPort(declaredPortsByAgent, agentKey, addition.Decision))
                    {
                        errors.Add(
                            $"additionalMocks['{agentKey}'][{j}]: decision '{addition.Decision}' is not "
                            + $"declared as an output port for agent '{agentKey}' in workflow {targetWorkflowDisplayLabel}.");
                        continue;
                    }

                    queue.Add(addition);
                }
            }
        }

        var snapshot = working.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<DryRunMockResponse>)kv.Value.ToArray(),
            StringComparer.Ordinal);

        return new ApplyResult(snapshot, errors);
    }

    /// <summary>
    /// Walks <paramref name="rootWorkflow"/> and every Subflow/ReviewLoop body it transitively
    /// references to produce a per-agent set of declared output ports. The implicit
    /// <c>Failed</c> port is added to every agent regardless of whether the author declared it,
    /// since the runtime treats it as universally available.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, IReadOnlySet<string>>> BuildPortIndexAsync(
        Workflow rootWorkflow,
        IWorkflowRepository workflowRepository,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rootWorkflow);
        ArgumentNullException.ThrowIfNull(workflowRepository);

        var ports = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var visited = new HashSet<(string Key, int Version)>();

        await VisitAsync(rootWorkflow, ports, visited, workflowRepository, cancellationToken)
            .ConfigureAwait(false);

        return ports.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlySet<string>)kv.Value,
            StringComparer.Ordinal);
    }

    private static async Task VisitAsync(
        Workflow workflow,
        Dictionary<string, HashSet<string>> ports,
        HashSet<(string Key, int Version)> visited,
        IWorkflowRepository workflowRepository,
        CancellationToken cancellationToken)
    {
        if (!visited.Add((workflow.Key, workflow.Version)))
        {
            return;
        }

        foreach (var node in workflow.Nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (node.Kind == WorkflowNodeKind.Agent || node.Kind == WorkflowNodeKind.Hitl)
            {
                if (string.IsNullOrWhiteSpace(node.AgentKey))
                {
                    continue;
                }

                if (!ports.TryGetValue(node.AgentKey, out var portSet))
                {
                    portSet = new HashSet<string>(StringComparer.Ordinal);
                    ports[node.AgentKey] = portSet;
                }

                foreach (var port in node.OutputPorts)
                {
                    if (!string.IsNullOrWhiteSpace(port))
                    {
                        portSet.Add(port);
                    }
                }

                portSet.Add("Failed");
            }
            else if (node.Kind == WorkflowNodeKind.Subflow || node.Kind == WorkflowNodeKind.ReviewLoop)
            {
                if (string.IsNullOrWhiteSpace(node.SubflowKey) || node.SubflowVersion is not int subVersion)
                {
                    continue;
                }

                var child = await workflowRepository
                    .GetAsync(node.SubflowKey, subVersion, cancellationToken)
                    .ConfigureAwait(false);
                if (child is not null)
                {
                    await VisitAsync(child, ports, visited, workflowRepository, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
    }

    private static bool AgentDeclaresPort(
        IReadOnlyDictionary<string, IReadOnlySet<string>> portsByAgent,
        string agentKey,
        string? decision)
    {
        if (string.IsNullOrEmpty(decision))
        {
            return false;
        }

        return portsByAgent.TryGetValue(agentKey, out var ports) && ports.Contains(decision);
    }
}
