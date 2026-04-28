using CodeFlow.Persistence;

namespace CodeFlow.Api.CascadeBump;

/// <summary>
/// E4: applies a cascade-bump plan by creating a new version of every workflow that pins a
/// bumped entity. Re-plans against the live DB rather than trusting a client-supplied plan,
/// so racing edits between preview and apply can't poison the cascade. Each workflow is
/// created in its own transaction (<see cref="IWorkflowRepository.CreateNewVersionAsync"/>);
/// applies are sequential in topological order so each parent reads the just-created
/// version of its child.
/// </summary>
public sealed class CascadeBumpExecutor(
    CascadeBumpPlanner planner,
    IWorkflowRepository workflowRepository)
{
    public async Task<CascadeBumpApplyResult> ApplyAsync(
        CascadeBumpRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var plan = await planner.PlanAsync(request, cancellationToken);

        var rewrittenAgentVersions = new Dictionary<string, int>(StringComparer.Ordinal);
        var rewrittenWorkflowVersions = new Dictionary<string, int>(StringComparer.Ordinal);

        // Seed rewrite maps from the root: any node pinning the root at vOld rewrites to vNew.
        if (plan.Root.Kind == CascadeBumpRootKind.Agent)
        {
            rewrittenAgentVersions[plan.Root.Key] = plan.Root.ToVersion;
        }
        else
        {
            rewrittenWorkflowVersions[plan.Root.Key] = plan.Root.ToVersion;
        }

        var applied = new List<CascadeBumpAppliedWorkflow>(plan.Steps.Count);
        var findings = new List<CascadeBumpFinding>(plan.Findings);

        foreach (var step in plan.Steps)
        {
            var current = await workflowRepository.GetLatestAsync(step.WorkflowKey, cancellationToken);
            if (current is null)
            {
                findings.Add(new CascadeBumpFinding(
                    Severity: "Warning",
                    Code: "WorkflowVanished",
                    Message: $"Workflow '{step.WorkflowKey}' could not be loaded for the cascade — "
                        + "skipped. The plan may have been invalidated by a concurrent delete."));
                continue;
            }

            var rewrittenNodes = current.Nodes
                .Select(node => RewriteNodePins(node, rewrittenAgentVersions, rewrittenWorkflowVersions))
                .ToArray();

            var draft = new WorkflowDraft(
                Key: current.Key,
                Name: current.Name,
                MaxRoundsPerRound: current.MaxRoundsPerRound,
                Category: current.Category,
                Tags: current.TagsOrEmpty.ToArray(),
                Nodes: rewrittenNodes,
                Edges: current.Edges
                    .Select(edge => new WorkflowEdgeDraft(
                        FromNodeId: edge.FromNodeId,
                        FromPort: edge.FromPort,
                        ToNodeId: edge.ToNodeId,
                        ToPort: edge.ToPort,
                        RotatesRound: edge.RotatesRound,
                        SortOrder: edge.SortOrder,
                        IntentionalBackedge: edge.IntentionalBackedge))
                    .ToArray(),
                Inputs: current.Inputs
                    .Select(input => new WorkflowInputDraft(
                        Key: input.Key,
                        DisplayName: input.DisplayName,
                        Kind: input.Kind,
                        Required: input.Required,
                        DefaultValueJson: input.DefaultValueJson,
                        Description: input.Description,
                        Ordinal: input.Ordinal))
                    .ToArray());

            var newVersion = await workflowRepository.CreateNewVersionAsync(draft, cancellationToken);

            // Future cascade layers must rewrite to the **actually created** version (which
            // may differ from the planned ToVersion if a racing edit landed mid-cascade).
            rewrittenWorkflowVersions[current.Key] = newVersion;

            applied.Add(new CascadeBumpAppliedWorkflow(
                WorkflowKey: current.Key,
                FromVersion: current.Version,
                CreatedVersion: newVersion,
                PinChanges: step.PinChanges));
        }

        return new CascadeBumpApplyResult(plan.Root, applied, findings);
    }

    private static WorkflowNodeDraft RewriteNodePins(
        WorkflowNode node,
        IReadOnlyDictionary<string, int> rewrittenAgents,
        IReadOnlyDictionary<string, int> rewrittenWorkflows)
    {
        var agentVersion = node.AgentVersion;
        if (!string.IsNullOrWhiteSpace(node.AgentKey)
            && agentVersion is int currentAgentVersion
            && rewrittenAgents.TryGetValue(node.AgentKey, out var newAgentVersion)
            && newAgentVersion != currentAgentVersion)
        {
            agentVersion = newAgentVersion;
        }

        var subflowVersion = node.SubflowVersion;
        if (!string.IsNullOrWhiteSpace(node.SubflowKey)
            && subflowVersion is int currentSubflowVersion
            && rewrittenWorkflows.TryGetValue(node.SubflowKey, out var newSubflowVersion)
            && newSubflowVersion != currentSubflowVersion)
        {
            subflowVersion = newSubflowVersion;
        }

        var contributorVersion = node.ContributorAgentVersion;
        if (!string.IsNullOrWhiteSpace(node.ContributorAgentKey)
            && contributorVersion is int currentContributorVersion
            && rewrittenAgents.TryGetValue(node.ContributorAgentKey, out var newContributorVersion)
            && newContributorVersion != currentContributorVersion)
        {
            contributorVersion = newContributorVersion;
        }

        var synthesizerVersion = node.SynthesizerAgentVersion;
        if (!string.IsNullOrWhiteSpace(node.SynthesizerAgentKey)
            && synthesizerVersion is int currentSynthesizerVersion
            && rewrittenAgents.TryGetValue(node.SynthesizerAgentKey, out var newSynthesizerVersion)
            && newSynthesizerVersion != currentSynthesizerVersion)
        {
            synthesizerVersion = newSynthesizerVersion;
        }

        var coordinatorVersion = node.CoordinatorAgentVersion;
        if (!string.IsNullOrWhiteSpace(node.CoordinatorAgentKey)
            && coordinatorVersion is int currentCoordinatorVersion
            && rewrittenAgents.TryGetValue(node.CoordinatorAgentKey, out var newCoordinatorVersion)
            && newCoordinatorVersion != currentCoordinatorVersion)
        {
            coordinatorVersion = newCoordinatorVersion;
        }

        return new WorkflowNodeDraft(
            Id: node.Id,
            Kind: node.Kind,
            AgentKey: node.AgentKey,
            AgentVersion: agentVersion,
            OutputScript: node.OutputScript,
            OutputPorts: node.OutputPorts,
            LayoutX: node.LayoutX,
            LayoutY: node.LayoutY,
            SubflowKey: node.SubflowKey,
            SubflowVersion: subflowVersion,
            ReviewMaxRounds: node.ReviewMaxRounds,
            LoopDecision: node.LoopDecision,
            InputScript: node.InputScript,
            OptOutLastRoundReminder: node.OptOutLastRoundReminder,
            RejectionHistory: node.RejectionHistory,
            MirrorOutputToWorkflowVar: node.MirrorOutputToWorkflowVar,
            OutputPortReplacements: node.OutputPortReplacements,
            Template: node.Template,
            OutputType: node.OutputType,
            SwarmProtocol: node.SwarmProtocol,
            SwarmN: node.SwarmN,
            ContributorAgentKey: node.ContributorAgentKey,
            ContributorAgentVersion: contributorVersion,
            SynthesizerAgentKey: node.SynthesizerAgentKey,
            SynthesizerAgentVersion: synthesizerVersion,
            CoordinatorAgentKey: node.CoordinatorAgentKey,
            CoordinatorAgentVersion: coordinatorVersion,
            SwarmTokenBudget: node.SwarmTokenBudget);
    }
}
