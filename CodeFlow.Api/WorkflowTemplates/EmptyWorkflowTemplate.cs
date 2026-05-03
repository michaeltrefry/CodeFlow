using CodeFlow.Persistence;

namespace CodeFlow.Api.WorkflowTemplates;

/// <summary>
/// S3 stub: materializes a single-node workflow with one Start node referencing a placeholder
/// agent the author can swap out. Proves the materialization plumbing end-to-end without
/// pulling in any partial / role / subflow dependencies (those land in S4-S7).
///
/// Materialized entities (with NamePrefix = "demo"):
/// <list type="bullet">
///   <item><description>Agent <c>demo-start</c> v1 — bare openai/gpt-test config the author
///   replaces with their own model + prompt.</description></item>
///   <item><description>Workflow <c>demo</c> v1 — one Start node wired to no edges (Failed
///   port flows down the implicit terminal).</description></item>
/// </list>
/// </summary>
internal static class EmptyWorkflowTemplate
{
    public static WorkflowTemplate Build() => new(
        Id: WorkflowTemplateRegistry.EmptyWorkflowId,
        Name: "Empty workflow",
        Description: "A blank workflow with one Start node and a placeholder agent. Use when "
            + "you want full control over the structure without scaffolding decisions.",
        Category: WorkflowTemplateCategory.Empty,
        Materialize: MaterializeAsync,
        PlanKeys: prefix => new[]
        {
            new PlannedEntityKey(MaterializedEntityKind.Agent, $"{prefix}-start"),
            new PlannedEntityKey(MaterializedEntityKind.Workflow, prefix),
        });

    private static async Task<MaterializedTemplateResult> MaterializeAsync(
        TemplateMaterializationContext context)
    {
        var agentKey = $"{context.NamePrefix}-start";
        var workflowKey = context.NamePrefix;

        const string agentConfigJson = """
            {
                "type": "agent",
                "name": "Start agent",
                "provider": "openai",
                "model": "gpt-test",
                "systemPrompt": "Replace this prompt with your starting instructions.",
                "outputs": [
                    { "kind": "Completed" }
                ]
            }
            """;

        var agentVersion = await context.AgentRepository.CreateNewVersionAsync(
            agentKey,
            agentConfigJson,
            context.CreatedBy,
            context.CancellationToken);

        var startNodeId = Guid.NewGuid();
        var draft = new WorkflowDraft(
            Key: workflowKey,
            Name: $"{context.NamePrefix} (from template)",
            MaxRoundsPerRound: 3,
            Nodes: new[]
            {
                new WorkflowNodeDraft(
                    Id: startNodeId,
                    Kind: WorkflowNodeKind.Start,
                    AgentKey: agentKey,
                    AgentVersion: agentVersion,
                    OutputScript: null,
                    OutputPorts: new[] { "Completed" },
                    LayoutX: 0,
                    LayoutY: 0),
            },
            Edges: Array.Empty<WorkflowEdgeDraft>(),
            Inputs: Array.Empty<WorkflowInputDraft>());

        var workflowVersion = await context.WorkflowRepository.CreateNewVersionAsync(
            draft, context.CancellationToken);

        return new MaterializedTemplateResult(
            EntryWorkflowKey: workflowKey,
            EntryWorkflowVersion: workflowVersion,
            CreatedEntities: new[]
            {
                new MaterializedEntity(MaterializedEntityKind.Agent, agentKey, agentVersion),
                new MaterializedEntity(MaterializedEntityKind.Workflow, workflowKey, workflowVersion),
            });
    }
}
