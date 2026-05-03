using CodeFlow.Persistence;

namespace CodeFlow.Api.WorkflowTemplates;

/// <summary>
/// S4: scaffold the doc's canonical "draft, critique, finalize" pattern in one click.
///
/// For prefix <c>my-feature</c>, materializes four entities:
/// <list type="bullet">
///   <item><description>Agent <c>my-feature-trigger</c> v1 — a minimal kickoff agent the
///   outer workflow's Start node invokes. Echoes the input through to the ReviewLoop body.
///   </description></item>
///   <item><description>Agent <c>my-feature-producer</c> v1 — pins
///   <c>@codeflow/producer-base</c> v1; system prompt has a "describe what you're producing"
///   slot the author fills in.</description></item>
///   <item><description>Agent <c>my-feature-reviewer</c> v1 — pins
///   <c>@codeflow/reviewer-base</c> v1; declares <c>Approved</c> + <c>Rejected</c> outputs;
///   system prompt has an "acceptance criteria" slot.</description></item>
///   <item><description>Workflow <c>my-feature-inner</c> v1 — Start (producer) →
///   reviewer with ports Approved / Rejected / Failed.</description></item>
///   <item><description>Workflow <c>my-feature</c> v1 — Start (trigger) → ReviewLoop pointing
///   at <c>my-feature-inner</c>, configured with <c>loopDecision = "Rejected"</c>,
///   <c>reviewMaxRounds = 5</c>, and <c>rejectionHistory.Enabled = true</c> so reviewer/producer
///   see <c>{{ rejectionHistory }}</c> on iteration.</description></item>
/// </list>
/// </summary>
internal static class ReviewLoopPairTemplate
{
    public const string Id = "review-loop-pair";

    public static WorkflowTemplate Build() => new(
        Id: Id,
        Name: "ReviewLoop pair",
        Description: "A producer agent and a reviewer agent wired into a bounded review loop. "
            + "The producer drafts content; the reviewer Approves or Rejects with feedback; "
            + "the loop iterates on Rejected until Approved or the round budget exhausts. Uses "
            + "the platform's stock @codeflow/producer-base and @codeflow/reviewer-base partials.",
        Category: WorkflowTemplateCategory.ReviewLoop,
        Materialize: MaterializeAsync,
        PlanKeys: prefix => new[]
        {
            new PlannedEntityKey(MaterializedEntityKind.Agent, $"{prefix}-trigger"),
            new PlannedEntityKey(MaterializedEntityKind.Agent, $"{prefix}-producer"),
            new PlannedEntityKey(MaterializedEntityKind.Agent, $"{prefix}-reviewer"),
            new PlannedEntityKey(MaterializedEntityKind.Workflow, $"{prefix}-inner"),
            new PlannedEntityKey(MaterializedEntityKind.Workflow, prefix),
        });

    private static async Task<MaterializedTemplateResult> MaterializeAsync(
        TemplateMaterializationContext context)
    {
        var prefix = context.NamePrefix;
        var triggerKey = $"{prefix}-trigger";
        var producerKey = $"{prefix}-producer";
        var reviewerKey = $"{prefix}-reviewer";
        var innerKey = $"{prefix}-inner";
        var outerKey = prefix;

        var triggerVersion = await context.AgentRepository.CreateNewVersionAsync(
            triggerKey, BuildTriggerConfigJson(), context.CreatedBy, context.CancellationToken);

        var producerVersion = await context.AgentRepository.CreateNewVersionAsync(
            producerKey, BuildProducerConfigJson(), context.CreatedBy, context.CancellationToken);

        var reviewerVersion = await context.AgentRepository.CreateNewVersionAsync(
            reviewerKey, BuildReviewerConfigJson(), context.CreatedBy, context.CancellationToken);

        var innerVersion = await context.WorkflowRepository.CreateNewVersionAsync(
            BuildInnerWorkflow(innerKey, producerKey, producerVersion, reviewerKey, reviewerVersion),
            context.CancellationToken);

        var outerVersion = await context.WorkflowRepository.CreateNewVersionAsync(
            BuildOuterWorkflow(outerKey, triggerKey, triggerVersion, innerKey, innerVersion),
            context.CancellationToken);

        return new MaterializedTemplateResult(
            EntryWorkflowKey: outerKey,
            EntryWorkflowVersion: outerVersion,
            CreatedEntities: new[]
            {
                new MaterializedEntity(MaterializedEntityKind.Agent, triggerKey, triggerVersion),
                new MaterializedEntity(MaterializedEntityKind.Agent, producerKey, producerVersion),
                new MaterializedEntity(MaterializedEntityKind.Agent, reviewerKey, reviewerVersion),
                new MaterializedEntity(MaterializedEntityKind.Workflow, innerKey, innerVersion),
                new MaterializedEntity(MaterializedEntityKind.Workflow, outerKey, outerVersion),
            });
    }

    private static string BuildTriggerConfigJson() =>
        """
        {
            "type": "agent",
            "name": "Trigger",
            "provider": "openai",
            "model": "gpt-test",
            "systemPrompt": "You are the kickoff agent for a ReviewLoop workflow. Your only job is to forward the workflow input to the body of the loop verbatim. Write the input back as your message content, then submit on the Continue port.",
            "promptTemplate": "{{ input }}",
            "outputs": [
                { "kind": "Continue" }
            ]
        }
        """;

    private static string BuildProducerConfigJson() =>
        """
        {
            "type": "agent",
            "name": "Producer",
            "provider": "openai",
            "model": "gpt-test",
            "systemPrompt": "You are the producer in a draft-and-review loop.\n\n{{ include \"@codeflow/producer-base\" }}\n\n## What you are producing\n\nTODO: describe the artifact this producer creates (e.g. \"a markdown summary of the input ticket\", \"a code patch that addresses the request\", \"a structured plan with phases and tasks\"). Replace this section with concrete instructions before running.\n\n## Inputs you receive\n\n- `{{ input }}` — the request from the outer trigger.\n- `{{ rejectionHistory }}` — accumulated reviewer feedback from prior rounds (empty on round 1).\n\nProduce the artifact, then submit on the Continue port.",
            "promptTemplate": "## Request\n{{ input }}\n\n{{ if rejectionHistory }}## Prior reviewer feedback (do NOT re-do work the reviewer has already accepted)\n{{ rejectionHistory }}\n{{ end }}",
            "partialPins": [
                { "key": "@codeflow/producer-base", "version": 1 }
            ],
            "outputs": [
                { "kind": "Continue" }
            ]
        }
        """;

    private static string BuildReviewerConfigJson() =>
        """
        {
            "type": "agent",
            "name": "Reviewer",
            "provider": "openai",
            "model": "gpt-test",
            "systemPrompt": "You are the reviewer in a draft-and-review loop.\n\n{{ include \"@codeflow/reviewer-base\" }}\n\n## Acceptance criteria\n\nTODO: describe the explicit criteria the producer's artifact must satisfy. Replace this section with a checklist before running. Examples:\n- \"All requirements from the request are addressed.\"\n- \"Code compiles and passes the cited tests.\"\n- \"Markdown structure follows the template described in {{ workflow.styleGuide }}.\"\n\n## Decision\n\n- Submit `Approved` when the artifact meets the criteria. Write a brief approval rationale as your message content.\n- Submit `Rejected` with concrete, actionable feedback the producer can address in the next round.",
            "promptTemplate": "## Producer's submission (round {{ round }} of {{ maxRounds }})\n{{ input }}",
            "partialPins": [
                { "key": "@codeflow/reviewer-base", "version": 1 }
            ],
            "outputs": [
                { "kind": "Approved" },
                { "kind": "Rejected" }
            ]
        }
        """;

    private static WorkflowDraft BuildInnerWorkflow(
        string innerKey, string producerKey, int producerVersion,
        string reviewerKey, int reviewerVersion)
    {
        var startNodeId = Guid.NewGuid();
        var reviewerNodeId = Guid.NewGuid();

        return new WorkflowDraft(
            Key: innerKey,
            Name: $"{innerKey} (producer + reviewer)",
            MaxRoundsPerRound: 5,
            Nodes: new[]
            {
                new WorkflowNodeDraft(
                    Id: startNodeId,
                    Kind: WorkflowNodeKind.Start,
                    AgentKey: producerKey,
                    AgentVersion: producerVersion,
                    OutputScript: null,
                    OutputPorts: new[] { "Continue" },
                    LayoutX: 0, LayoutY: 0),
                new WorkflowNodeDraft(
                    Id: reviewerNodeId,
                    Kind: WorkflowNodeKind.Agent,
                    AgentKey: reviewerKey,
                    AgentVersion: reviewerVersion,
                    OutputScript: null,
                    OutputPorts: new[] { "Approved", "Rejected" },
                    LayoutX: 250, LayoutY: 0),
            },
            Edges: new[]
            {
                new WorkflowEdgeDraft(
                    FromNodeId: startNodeId,
                    FromPort: "Continue",
                    ToNodeId: reviewerNodeId,
                    ToPort: WorkflowEdge.DefaultInputPort,
                    RotatesRound: false,
                    SortOrder: 0),
            },
            Inputs: Array.Empty<WorkflowInputDraft>());
    }

    private static WorkflowDraft BuildOuterWorkflow(
        string outerKey, string triggerKey, int triggerVersion,
        string innerKey, int innerVersion)
    {
        var startNodeId = Guid.NewGuid();
        var loopNodeId = Guid.NewGuid();

        return new WorkflowDraft(
            Key: outerKey,
            Name: $"{outerKey} (review loop)",
            MaxRoundsPerRound: 5,
            Nodes: new[]
            {
                new WorkflowNodeDraft(
                    Id: startNodeId,
                    Kind: WorkflowNodeKind.Start,
                    AgentKey: triggerKey,
                    AgentVersion: triggerVersion,
                    OutputScript: null,
                    // `Failed` is implicit on every node; declaring it trips
                    // WorkflowValidator's reserved-port rule on edit.
                    OutputPorts: new[] { "Continue" },
                    LayoutX: 0, LayoutY: 0),
                new WorkflowNodeDraft(
                    Id: loopNodeId,
                    Kind: WorkflowNodeKind.ReviewLoop,
                    AgentKey: null,
                    AgentVersion: null,
                    OutputScript: null,
                    // ReviewLoop synthesizes `Exhausted` at runtime and `Failed` is implicit on
                    // every node. Only the loopDecision-derived port (`Approved` here) goes here.
                    OutputPorts: new[] { "Approved" },
                    LayoutX: 250, LayoutY: 0,
                    SubflowKey: innerKey,
                    SubflowVersion: innerVersion,
                    ReviewMaxRounds: 5,
                    LoopDecision: "Rejected",
                    RejectionHistory: new RejectionHistoryConfig(Enabled: true)),
            },
            Edges: new[]
            {
                new WorkflowEdgeDraft(
                    FromNodeId: startNodeId,
                    FromPort: "Continue",
                    ToNodeId: loopNodeId,
                    ToPort: WorkflowEdge.DefaultInputPort,
                    RotatesRound: false,
                    SortOrder: 0),
            },
            Inputs: Array.Empty<WorkflowInputDraft>());
    }
}
