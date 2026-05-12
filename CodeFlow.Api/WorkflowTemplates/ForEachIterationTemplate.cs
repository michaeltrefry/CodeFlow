using CodeFlow.Persistence;

namespace CodeFlow.Api.WorkflowTemplates;

/// <summary>
/// sc-946 / FE-5: scaffold a producer → ForEach → summarizer demo so authors discover the
/// <see cref="WorkflowNodeKind.ForEach"/> node and learn the canonical loop.item / loop.index /
/// loop.count / loop.isLast prompt bindings.
///
/// For prefix <c>my-loop</c>, materializes five entities:
/// <list type="bullet">
///   <item><description>Agent <c>my-loop-producer</c> v1 — the outer workflow's Start node;
///   its output script seeds a small demo array onto <c>workflow.demoItems</c> so the
///   ForEach node has something to iterate over. Authors swap the seed list (or the entire
///   script) for a real upstream feed.</description></item>
///   <item><description>Agent <c>my-loop-item</c> v1 — the child workflow's only agent; reads
///   <c>{{ loop.item }}</c> / <c>{{ loop.index }}</c> / <c>{{ loop.count }}</c> /
///   <c>{{ loop.isLast }}</c> from the runtime-bound iteration scope.</description></item>
///   <item><description>Agent <c>my-loop-summarizer</c> v1 — receives the aggregate JSON
///   array of per-item outputs the ForEach node emits on Continue and writes a one-paragraph
///   summary.</description></item>
///   <item><description>Workflow <c>my-loop-per-item</c> v1 — Start (my-loop-item). The
///   ForEach node spawns one child saga per iteration against this workflow.</description></item>
///   <item><description>Workflow <c>my-loop</c> v1 — Start (producer) → ForEach over
///   <c>workflow.demoItems</c> → Agent (summarizer). Demonstrates the full producer / iterate /
///   reduce shape end-to-end.</description></item>
/// </list>
/// </summary>
internal static class ForEachIterationTemplate
{
    public const string Id = "foreach-iteration";

    public static WorkflowTemplate Build() => new(
        Id: Id,
        Name: "ForEach iteration",
        Description: "A producer agent that seeds a small list, a ForEach node that runs a child "
            + "subflow once per item, and a summarizer agent that reduces the per-item outputs into "
            + "a final artifact. The child agent reads loop.item / loop.index / loop.count / "
            + "loop.isLast from its prompt scope — the canonical shape for any iterate-over-a-known-"
            + "collection workflow (per-repo fan-out, batch processing, task-list dispatch).",
        Category: WorkflowTemplateCategory.Other,
        Materialize: MaterializeAsync,
        PlanKeys: prefix => new[]
        {
            new PlannedEntityKey(MaterializedEntityKind.Agent, $"{prefix}-producer"),
            new PlannedEntityKey(MaterializedEntityKind.Agent, $"{prefix}-item"),
            new PlannedEntityKey(MaterializedEntityKind.Agent, $"{prefix}-summarizer"),
            new PlannedEntityKey(MaterializedEntityKind.Workflow, $"{prefix}-per-item"),
            new PlannedEntityKey(MaterializedEntityKind.Workflow, prefix),
        });

    private static async Task<MaterializedTemplateResult> MaterializeAsync(
        TemplateMaterializationContext context)
    {
        var prefix = context.NamePrefix;
        var producerKey = $"{prefix}-producer";
        var itemKey = $"{prefix}-item";
        var summarizerKey = $"{prefix}-summarizer";
        var perItemKey = $"{prefix}-per-item";
        var outerKey = prefix;

        var producerVersion = await context.AgentRepository.CreateNewVersionAsync(
            producerKey, BuildProducerConfigJson(), context.CreatedBy, context.CancellationToken);

        var itemVersion = await context.AgentRepository.CreateNewVersionAsync(
            itemKey, BuildItemConfigJson(), context.CreatedBy, context.CancellationToken);

        var summarizerVersion = await context.AgentRepository.CreateNewVersionAsync(
            summarizerKey, BuildSummarizerConfigJson(), context.CreatedBy, context.CancellationToken);

        var perItemVersion = await context.WorkflowRepository.CreateNewVersionAsync(
            BuildPerItemWorkflow(perItemKey, itemKey, itemVersion),
            context.CancellationToken);

        var outerVersion = await context.WorkflowRepository.CreateNewVersionAsync(
            BuildOuterWorkflow(outerKey, producerKey, producerVersion, summarizerKey, summarizerVersion, perItemKey, perItemVersion),
            context.CancellationToken);

        return new MaterializedTemplateResult(
            EntryWorkflowKey: outerKey,
            EntryWorkflowVersion: outerVersion,
            CreatedEntities: new[]
            {
                new MaterializedEntity(MaterializedEntityKind.Agent, producerKey, producerVersion),
                new MaterializedEntity(MaterializedEntityKind.Agent, itemKey, itemVersion),
                new MaterializedEntity(MaterializedEntityKind.Agent, summarizerKey, summarizerVersion),
                new MaterializedEntity(MaterializedEntityKind.Workflow, perItemKey, perItemVersion),
                new MaterializedEntity(MaterializedEntityKind.Workflow, outerKey, outerVersion),
            });
    }

    private static string BuildProducerConfigJson() =>
        """
        {
            "type": "agent",
            "name": "ForEach Producer",
            "provider": "openai",
            "model": "gpt-test",
            "systemPrompt": "You are the producer in a ForEach demo workflow.\n\n## What you do\n\nYou are seeded with a fixed demo list by the Start node's output script (see the workflow's outputScript on the Start node). Your only job here is to acknowledge the workflow input — the real per-item work happens in the downstream ForEach child workflow.\n\nWrite a one-line acknowledgement message, then submit on Continue.\n\n## Customizing for real use\n\n- Replace the Start node's output script with one that derives the iteration list from a real source — `setWorkflow('demoItems', [...])` accepts any JSON-serializable array.\n- Replace this prompt with concrete instructions for how to compute the iteration list when the producer is a real LLM step (e.g. \"analyze the input ticket and emit a list of subtasks\").",
            "promptTemplate": "## Workflow input\n{{ input }}\n\nAcknowledge with a single sentence describing what's about to run, then submit on Continue.",
            "outputs": [
                { "kind": "Continue" }
            ]
        }
        """;

    private static string BuildItemConfigJson() =>
        """
        {
            "type": "agent",
            "name": "ForEach Item Processor",
            "provider": "openai",
            "model": "gpt-test",
            "systemPrompt": "You are the per-iteration agent inside a ForEach child workflow.\n\n## Iteration context\n\nThe runtime binds these top-level template variables for every iteration:\n- `{{ loop.item }}` — the JSON-encoded payload for this iteration's item.\n- `{{ loop.index }}` — the 0-based iteration index.\n- `{{ loop.count }}` — the total number of iterations.\n- `{{ loop.isLast }}` — `true` on the final iteration, `false` otherwise.\n\n## What you do\n\nProcess the item and emit a short result. The runtime collects your output and appends it to a per-iteration outputs array the parent's downstream summarizer agent sees.\n\nCustomize the prompt below with concrete instructions (e.g. \"summarize the article at {{ loop.item }}\", \"draft a code patch for task {{ loop.item }}\").",
            "promptTemplate": "## Iteration {{ loop.index }} of {{ loop.count }}{{ if loop.isLast == \"true\" }} (final){{ end }}\n\n### Item\n{{ loop.item }}\n\nProcess the item and submit on Completed with your result as the message body.",
            "outputs": [
                { "kind": "Completed" }
            ]
        }
        """;

    private static string BuildSummarizerConfigJson() =>
        """
        {
            "type": "agent",
            "name": "ForEach Summarizer",
            "provider": "openai",
            "model": "gpt-test",
            "systemPrompt": "You are the summarizer downstream of a ForEach iteration.\n\n## What you receive\n\nThe ForEach node emits a JSON array describing every iteration:\n```\n[\n  { \"index\": 0, \"outputRef\": \"artifact://...\", \"port\": \"Completed\" },\n  { \"index\": 1, \"outputRef\": \"artifact://...\", \"port\": \"Completed\" }\n]\n```\nEach `outputRef` points at the per-item agent's output artifact for that iteration. The list arrives verbatim on your input.\n\n## What you do\n\nWrite a one-paragraph summary of the run: how many iterations completed, whether they all succeeded, and any pattern worth flagging. Submit on Completed when done.",
            "promptTemplate": "## ForEach run summary\n\n### Aggregate outputs (JSON)\n```json\n{{ input }}\n```\n\nWrite the summary paragraph and submit on Completed.",
            "outputs": [
                { "kind": "Completed" }
            ]
        }
        """;

    /// <summary>
    /// Seed-collection output script attached to the outer workflow's Start node. Authors customise
    /// this — or replace it entirely with logic that reads from <c>context.*</c> / <c>workflow.*</c>
    /// — to feed the ForEach node a real list.
    /// </summary>
    private const string SeedDemoItemsScript = """
        // Seed a tiny demo list onto workflow.demoItems so the downstream ForEach node has
        // something to iterate over. Replace the literal array with logic that derives the
        // iteration list from real context (e.g. parse input JSON, look up workflow.* keys,
        // or branch on context.* fields).
        setWorkflow('demoItems', ['red', 'green', 'blue']);
        setNodePath('Continue');
        """;

    private static WorkflowDraft BuildPerItemWorkflow(
        string perItemKey, string itemKey, int itemVersion)
    {
        var startNodeId = Guid.NewGuid();

        return new WorkflowDraft(
            Key: perItemKey,
            Name: $"{perItemKey} (per-item handler)",
            MaxStepsPerSaga: 5,
            Nodes: new[]
            {
                new WorkflowNodeDraft(
                    Id: startNodeId,
                    Kind: WorkflowNodeKind.Start,
                    AgentKey: itemKey,
                    AgentVersion: itemVersion,
                    OutputScript: null,
                    OutputPorts: new[] { "Completed" },
                    LayoutX: 0, LayoutY: 0),
            },
            Edges: Array.Empty<WorkflowEdgeDraft>(),
            Inputs: Array.Empty<WorkflowInputDraft>());
    }

    private static WorkflowDraft BuildOuterWorkflow(
        string outerKey,
        string producerKey, int producerVersion,
        string summarizerKey, int summarizerVersion,
        string perItemKey, int perItemVersion)
    {
        var startNodeId = Guid.NewGuid();
        var forEachNodeId = Guid.NewGuid();
        var summarizerNodeId = Guid.NewGuid();

        return new WorkflowDraft(
            Key: outerKey,
            Name: $"{outerKey} (ForEach demo)",
            MaxStepsPerSaga: 20,
            WorkflowVarsWrites: new[] { "demoItems" },
            Nodes: new[]
            {
                new WorkflowNodeDraft(
                    Id: startNodeId,
                    Kind: WorkflowNodeKind.Start,
                    AgentKey: producerKey,
                    AgentVersion: producerVersion,
                    OutputScript: SeedDemoItemsScript,
                    OutputPorts: new[] { "Continue" },
                    LayoutX: 0, LayoutY: 0),
                new WorkflowNodeDraft(
                    Id: forEachNodeId,
                    Kind: WorkflowNodeKind.ForEach,
                    AgentKey: null,
                    AgentVersion: null,
                    OutputScript: null,
                    // ForEach synthesizes Continue + Failed — authors must not declare any ports.
                    OutputPorts: Array.Empty<string>(),
                    LayoutX: 300, LayoutY: 0,
                    SubflowKey: perItemKey,
                    SubflowVersion: perItemVersion,
                    CollectionExpression: "workflow.demoItems",
                    ItemVar: "item"),
                new WorkflowNodeDraft(
                    Id: summarizerNodeId,
                    Kind: WorkflowNodeKind.Agent,
                    AgentKey: summarizerKey,
                    AgentVersion: summarizerVersion,
                    OutputScript: null,
                    OutputPorts: new[] { "Completed" },
                    LayoutX: 600, LayoutY: 0),
            },
            Edges: new[]
            {
                new WorkflowEdgeDraft(
                    FromNodeId: startNodeId,
                    FromPort: "Continue",
                    ToNodeId: forEachNodeId,
                    ToPort: WorkflowEdge.DefaultInputPort,
                    RotatesRound: false,
                    SortOrder: 0),
                new WorkflowEdgeDraft(
                    FromNodeId: forEachNodeId,
                    FromPort: "Continue",
                    ToNodeId: summarizerNodeId,
                    ToPort: WorkflowEdge.DefaultInputPort,
                    RotatesRound: false,
                    SortOrder: 1),
            },
            Inputs: Array.Empty<WorkflowInputDraft>());
    }
}
