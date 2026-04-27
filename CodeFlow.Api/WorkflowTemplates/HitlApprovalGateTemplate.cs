using CodeFlow.Persistence;

namespace CodeFlow.Api.WorkflowTemplates;

/// <summary>
/// S5: HITL approval gate scaffold. Drops in between subflows when an author wants a human
/// gate that just passes the upstream artifact through with a human Approved / Cancelled
/// decision.
///
/// For prefix <c>my-gate</c>, materializes 3 entities:
/// <list type="bullet">
///   <item><description>Agent <c>my-gate-trigger</c> v1 — minimal kickoff that forwards the
///   workflow input verbatim into the HITL form.</description></item>
///   <item><description>Hitl agent <c>my-gate-form</c> v1 — passthrough <c>outputTemplate:
///   "{{ input }}"</c>; <c>Approved</c> + <c>Cancelled</c> ports.</description></item>
///   <item><description>Workflow <c>my-gate</c> v1 — Start (trigger) → Hitl (form) with
///   the form's two ports as terminal exits.</description></item>
/// </list>
///
/// The author wires the resulting workflow into the parent graph as a Subflow node — the
/// terminal ports surface automatically per CodeFlow's "Subflow node ports are computed,
/// not authored" rule.
/// </summary>
internal static class HitlApprovalGateTemplate
{
    public const string Id = "hitl-approval-gate";

    public static WorkflowTemplate Build() => new(
        Id: Id,
        Name: "HITL approval gate",
        Description: "A standalone workflow with one human-in-the-loop gate. The operator sees "
            + "the upstream artifact and can either Approve (passes through) or Cancel (exits "
            + "without modification). Drop between subflows for human checkpoints.",
        Category: WorkflowTemplateCategory.Hitl,
        Materialize: MaterializeAsync,
        PlanKeys: prefix => new[]
        {
            new PlannedEntityKey(MaterializedEntityKind.Agent, $"{prefix}-trigger"),
            new PlannedEntityKey(MaterializedEntityKind.Agent, $"{prefix}-form"),
            new PlannedEntityKey(MaterializedEntityKind.Workflow, prefix),
        });

    private static async Task<MaterializedTemplateResult> MaterializeAsync(
        TemplateMaterializationContext context)
    {
        var prefix = context.NamePrefix;
        var triggerKey = $"{prefix}-trigger";
        var formKey = $"{prefix}-form";
        var workflowKey = prefix;

        var triggerVersion = await context.AgentRepository.CreateNewVersionAsync(
            triggerKey, BuildTriggerConfigJson(), context.CreatedBy, context.CancellationToken);

        var formVersion = await context.AgentRepository.CreateNewVersionAsync(
            formKey, BuildHitlFormConfigJson(), context.CreatedBy, context.CancellationToken);

        var workflowVersion = await context.WorkflowRepository.CreateNewVersionAsync(
            BuildWorkflow(workflowKey, triggerKey, triggerVersion, formKey, formVersion),
            context.CancellationToken);

        return new MaterializedTemplateResult(
            EntryWorkflowKey: workflowKey,
            EntryWorkflowVersion: workflowVersion,
            CreatedEntities: new[]
            {
                new MaterializedEntity(MaterializedEntityKind.Agent, triggerKey, triggerVersion),
                new MaterializedEntity(MaterializedEntityKind.Agent, formKey, formVersion),
                new MaterializedEntity(MaterializedEntityKind.Workflow, workflowKey, workflowVersion),
            });
    }

    private static string BuildTriggerConfigJson() =>
        """
        {
            "type": "agent",
            "name": "Trigger",
            "provider": "openai",
            "model": "gpt-test",
            "systemPrompt": "You are the kickoff agent for a HITL approval gate. Your only job is to forward the workflow input to the HITL form verbatim. Write the input back as your message content, then submit on the Continue port.",
            "promptTemplate": "{{ input }}",
            "outputs": [
                { "kind": "Continue" }
            ]
        }
        """;

    private static string BuildHitlFormConfigJson() =>
        """
        {
            "type": "hitl",
            "name": "Approval gate",
            "description": "Human-in-the-loop checkpoint. The operator sees the upstream artifact and chooses Approved (passes through unchanged) or Cancelled (exits the gate).",
            "outputTemplate": "{{ input }}",
            "outputs": [
                { "kind": "Approved", "description": "Operator approves; artifact passes through unchanged." },
                { "kind": "Cancelled", "description": "Operator cancels; downstream consumers should treat this as an explicit abort.", "contentOptional": true }
            ]
        }
        """;

    private static WorkflowDraft BuildWorkflow(
        string workflowKey, string triggerKey, int triggerVersion,
        string formKey, int formVersion)
    {
        var startNodeId = Guid.NewGuid();
        var hitlNodeId = Guid.NewGuid();

        return new WorkflowDraft(
            Key: workflowKey,
            Name: $"{workflowKey} (HITL approval gate)",
            MaxRoundsPerRound: 3,
            Nodes: new[]
            {
                new WorkflowNodeDraft(
                    Id: startNodeId,
                    Kind: WorkflowNodeKind.Start,
                    AgentKey: triggerKey,
                    AgentVersion: triggerVersion,
                    OutputScript: null,
                    OutputPorts: new[] { "Continue" },
                    LayoutX: 0, LayoutY: 0),
                new WorkflowNodeDraft(
                    Id: hitlNodeId,
                    Kind: WorkflowNodeKind.Hitl,
                    AgentKey: formKey,
                    AgentVersion: formVersion,
                    OutputScript: null,
                    OutputPorts: new[] { "Approved", "Cancelled" },
                    LayoutX: 250, LayoutY: 0),
            },
            Edges: new[]
            {
                new WorkflowEdgeDraft(
                    FromNodeId: startNodeId,
                    FromPort: "Continue",
                    ToNodeId: hitlNodeId,
                    ToPort: WorkflowEdge.DefaultInputPort,
                    RotatesRound: false,
                    SortOrder: 0),
            },
            Inputs: Array.Empty<WorkflowInputDraft>());
    }
}
