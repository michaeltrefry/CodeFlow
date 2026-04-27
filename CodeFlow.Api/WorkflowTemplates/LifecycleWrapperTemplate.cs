using CodeFlow.Persistence;

namespace CodeFlow.Api.WorkflowTemplates;

/// <summary>
/// S7: scaffold a multi-phase lifecycle workflow — chained subflows with HITL gates between
/// them. Codifies the shape of <c>lifecycle-v1</c> (PRD intake → impl-plan → dev-flow →
/// publish) for new workflows that need it.
///
/// Materializes (default 3 phases) for prefix <c>my-flow</c>:
/// <list type="bullet">
///   <item><description>Agent <c>my-flow-trigger</c> v1 — kickoff that forwards input to phase 1.</description></item>
///   <item><description>Agent <c>my-flow-phase-trigger</c> v1 — shared phase placeholder agent.
///   Each phase stub workflow uses this as its Start agent.</description></item>
///   <item><description>3 workflows <c>my-flow-phase-1/2/3</c> v1 — each is a single-node stub
///   with port <c>Completed</c>. Author replaces the workflow content (or repoints the outer
///   Subflow node) with the real phase workflow once the lifecycle is in place.</description></item>
///   <item><description>2 Hitl agents <c>my-flow-gate-1-form</c> / <c>-gate-2-form</c> v1 —
///   passthrough forms with Approved + Cancelled ports operators see between phases.</description></item>
///   <item><description>Workflow <c>my-flow</c> v1 — the lifecycle:
///   Start (trigger) → Subflow (phase-1) → Hitl (gate-1) → Subflow (phase-2) → Hitl (gate-2)
///   → Subflow (phase-3) → terminal. Each Subflow's <c>Completed</c> port is wired forward.</description></item>
/// </list>
///
/// Phase count is fixed at 3 in this materializer. Authors who want more phases can edit the
/// outer workflow post-materialization (add Subflow + Hitl pairs and rewire).
/// </summary>
internal static class LifecycleWrapperTemplate
{
    public const string Id = "lifecycle-wrapper";
    private const int DefaultPhaseCount = 3;

    public static WorkflowTemplate Build() => new(
        Id: Id,
        Name: "Lifecycle wrapper",
        Description: "A multi-phase lifecycle workflow: 3 placeholder Subflow nodes chained "
            + "by 2 HITL approval gates. Use as a starting shell when you need to compose "
            + "several existing workflows into a single end-to-end run with human checkpoints "
            + "between phases. Author replaces each Subflow node's pin with the real phase "
            + "workflow post-materialization.",
        Category: WorkflowTemplateCategory.Lifecycle,
        Materialize: MaterializeAsync,
        PlanKeys: prefix =>
        {
            var keys = new List<PlannedEntityKey>
            {
                new(MaterializedEntityKind.Agent, $"{prefix}-trigger"),
                new(MaterializedEntityKind.Agent, $"{prefix}-phase-trigger"),
            };
            for (var i = 1; i <= DefaultPhaseCount; i++)
            {
                keys.Add(new PlannedEntityKey(MaterializedEntityKind.Workflow, $"{prefix}-phase-{i}"));
            }
            for (var i = 1; i <= DefaultPhaseCount - 1; i++)
            {
                keys.Add(new PlannedEntityKey(MaterializedEntityKind.Agent, $"{prefix}-gate-{i}-form"));
            }
            keys.Add(new PlannedEntityKey(MaterializedEntityKind.Workflow, prefix));
            return keys;
        });

    private static async Task<MaterializedTemplateResult> MaterializeAsync(
        TemplateMaterializationContext context)
    {
        var prefix = context.NamePrefix;
        var triggerKey = $"{prefix}-trigger";
        var phaseTriggerKey = $"{prefix}-phase-trigger";

        var triggerVersion = await context.AgentRepository.CreateNewVersionAsync(
            triggerKey, BuildTriggerConfigJson(), context.CreatedBy, context.CancellationToken);

        var phaseTriggerVersion = await context.AgentRepository.CreateNewVersionAsync(
            phaseTriggerKey, BuildPhaseTriggerConfigJson(), context.CreatedBy, context.CancellationToken);

        var phaseWorkflowVersions = new List<(string Key, int Version)>();
        for (var i = 1; i <= DefaultPhaseCount; i++)
        {
            var phaseKey = $"{prefix}-phase-{i}";
            var phaseVersion = await context.WorkflowRepository.CreateNewVersionAsync(
                BuildPhaseStubWorkflow(phaseKey, i, phaseTriggerKey, phaseTriggerVersion),
                context.CancellationToken);
            phaseWorkflowVersions.Add((phaseKey, phaseVersion));
        }

        var gateAgents = new List<(string Key, int Version)>();
        for (var i = 1; i <= DefaultPhaseCount - 1; i++)
        {
            var gateKey = $"{prefix}-gate-{i}-form";
            var gateVersion = await context.AgentRepository.CreateNewVersionAsync(
                gateKey,
                BuildGateFormConfigJson(i, i + 1),
                context.CreatedBy,
                context.CancellationToken);
            gateAgents.Add((gateKey, gateVersion));
        }

        var lifecycleVersion = await context.WorkflowRepository.CreateNewVersionAsync(
            BuildLifecycleWorkflow(
                prefix,
                triggerKey, triggerVersion,
                phaseWorkflowVersions,
                gateAgents),
            context.CancellationToken);

        var entities = new List<MaterializedEntity>
        {
            new(MaterializedEntityKind.Agent, triggerKey, triggerVersion),
            new(MaterializedEntityKind.Agent, phaseTriggerKey, phaseTriggerVersion),
        };
        foreach (var (key, version) in phaseWorkflowVersions)
        {
            entities.Add(new MaterializedEntity(MaterializedEntityKind.Workflow, key, version));
        }
        foreach (var (key, version) in gateAgents)
        {
            entities.Add(new MaterializedEntity(MaterializedEntityKind.Agent, key, version));
        }
        entities.Add(new MaterializedEntity(MaterializedEntityKind.Workflow, prefix, lifecycleVersion));

        return new MaterializedTemplateResult(
            EntryWorkflowKey: prefix,
            EntryWorkflowVersion: lifecycleVersion,
            CreatedEntities: entities);
    }

    private static string BuildTriggerConfigJson() =>
        """
        {
            "type": "agent",
            "name": "Lifecycle Trigger",
            "provider": "openai",
            "model": "gpt-test",
            "systemPrompt": "You are the kickoff agent for a lifecycle workflow. Your only job is to forward the workflow input to the first phase verbatim. Write the input back as your message content, then submit on the Continue port.",
            "promptTemplate": "{{ input }}",
            "outputs": [
                { "kind": "Continue" }
            ]
        }
        """;

    private static string BuildPhaseTriggerConfigJson() =>
        """
        {
            "type": "agent",
            "name": "Phase Trigger (placeholder)",
            "provider": "openai",
            "model": "gpt-test",
            "systemPrompt": "You are a placeholder phase agent. The lifecycle template materialized this stub so the workflow can save and run end-to-end before the author wires real phase workflows. Your only job is to acknowledge the input and submit on the Completed port.\n\nReplace this entire phase workflow with your real phase logic — or rewire the Subflow node in the parent lifecycle to point at a different workflow.",
            "promptTemplate": "{{ input }}",
            "outputs": [
                { "kind": "Completed" }
            ]
        }
        """;

    private static string BuildGateFormConfigJson(int phaseFrom, int phaseTo) =>
        "{\n"
        + "    \"type\": \"hitl\",\n"
        + $"    \"name\": \"Gate {phaseFrom} → {phaseTo}\",\n"
        + $"    \"description\": \"Approval gate between phase {phaseFrom} and phase {phaseTo}. Operator reviews the upstream phase's output and chooses Approved (continue to phase {phaseTo}) or Cancelled (abort the lifecycle).\",\n"
        + "    \"outputTemplate\": \"{{ input }}\",\n"
        + "    \"outputs\": [\n"
        + $"        {{ \"kind\": \"Approved\", \"description\": \"Continue to phase {phaseTo}.\" }},\n"
        + "        { \"kind\": \"Cancelled\", \"description\": \"Abort the lifecycle.\", \"contentOptional\": true }\n"
        + "    ]\n"
        + "}";

    private static WorkflowDraft BuildPhaseStubWorkflow(
        string phaseKey,
        int phaseNumber,
        string phaseTriggerKey,
        int phaseTriggerVersion)
    {
        var startNodeId = Guid.NewGuid();
        return new WorkflowDraft(
            Key: phaseKey,
            Name: $"Phase {phaseNumber} (placeholder)",
            MaxRoundsPerRound: 3,
            Nodes: new[]
            {
                new WorkflowNodeDraft(
                    Id: startNodeId,
                    Kind: WorkflowNodeKind.Start,
                    AgentKey: phaseTriggerKey,
                    AgentVersion: phaseTriggerVersion,
                    OutputScript: null,
                    OutputPorts: new[] { "Completed" },
                    LayoutX: 0, LayoutY: 0),
            },
            Edges: Array.Empty<WorkflowEdgeDraft>(),
            Inputs: Array.Empty<WorkflowInputDraft>());
    }

    private static WorkflowDraft BuildLifecycleWorkflow(
        string lifecycleKey,
        string triggerKey, int triggerVersion,
        IReadOnlyList<(string Key, int Version)> phaseWorkflows,
        IReadOnlyList<(string Key, int Version)> gateAgents)
    {
        var startNodeId = Guid.NewGuid();
        var phaseNodeIds = phaseWorkflows.Select(_ => Guid.NewGuid()).ToArray();
        var gateNodeIds = gateAgents.Select(_ => Guid.NewGuid()).ToArray();

        var nodes = new List<WorkflowNodeDraft>
        {
            new(
                Id: startNodeId,
                Kind: WorkflowNodeKind.Start,
                AgentKey: triggerKey,
                AgentVersion: triggerVersion,
                OutputScript: null,
                OutputPorts: new[] { "Continue" },
                LayoutX: 0, LayoutY: 0),
        };

        for (var i = 0; i < phaseWorkflows.Count; i++)
        {
            var (key, version) = phaseWorkflows[i];
            nodes.Add(new WorkflowNodeDraft(
                Id: phaseNodeIds[i],
                Kind: WorkflowNodeKind.Subflow,
                AgentKey: null,
                AgentVersion: null,
                OutputScript: null,
                OutputPorts: new[] { "Completed" },
                LayoutX: 250 + (i * 500), LayoutY: 0,
                SubflowKey: key,
                SubflowVersion: version));

            if (i < gateAgents.Count)
            {
                var (gateKey, gateVersion) = gateAgents[i];
                nodes.Add(new WorkflowNodeDraft(
                    Id: gateNodeIds[i],
                    Kind: WorkflowNodeKind.Hitl,
                    AgentKey: gateKey,
                    AgentVersion: gateVersion,
                    OutputScript: null,
                    OutputPorts: new[] { "Approved", "Cancelled" },
                    LayoutX: 500 + (i * 500), LayoutY: 0));
            }
        }

        var edges = new List<WorkflowEdgeDraft>();
        var sortOrder = 0;

        // Start → first phase.
        edges.Add(new WorkflowEdgeDraft(
            FromNodeId: startNodeId,
            FromPort: "Continue",
            ToNodeId: phaseNodeIds[0],
            ToPort: WorkflowEdge.DefaultInputPort,
            RotatesRound: false,
            SortOrder: sortOrder++));

        // For each phase 1..N-1: phase-i.Completed → gate-i.in; gate-i.Approved → phase-(i+1).in.
        for (var i = 0; i < gateAgents.Count; i++)
        {
            edges.Add(new WorkflowEdgeDraft(
                FromNodeId: phaseNodeIds[i],
                FromPort: "Completed",
                ToNodeId: gateNodeIds[i],
                ToPort: WorkflowEdge.DefaultInputPort,
                RotatesRound: false,
                SortOrder: sortOrder++));

            edges.Add(new WorkflowEdgeDraft(
                FromNodeId: gateNodeIds[i],
                FromPort: "Approved",
                ToNodeId: phaseNodeIds[i + 1],
                ToPort: WorkflowEdge.DefaultInputPort,
                RotatesRound: false,
                SortOrder: sortOrder++));
        }

        // Final phase's Completed port is unwired → terminal exit.

        return new WorkflowDraft(
            Key: lifecycleKey,
            Name: $"{lifecycleKey} (lifecycle wrapper)",
            MaxRoundsPerRound: 5,
            Nodes: nodes,
            Edges: edges,
            Inputs: Array.Empty<WorkflowInputDraft>());
    }
}
