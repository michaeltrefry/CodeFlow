using CodeFlow.Persistence;

namespace CodeFlow.Api.WorkflowTemplates;

/// <summary>
/// S6: scaffold the doc's "setup agent before a loop" pattern in one click. A setup agent
/// seeds the workflow bag from the input, then a ReviewLoop iterates a producer/reviewer
/// pair against that bag, with an HITL escalation on the Exhausted port.
///
/// For prefix <c>my-feature</c>, materializes 6 entities:
/// <list type="bullet">
///   <item><description>Agent <c>my-feature-setup</c> v1 — Start setup agent. Has an empty
///   <c>inputScript</c> with a TODO comment block explaining where to seed globals from the
///   input.</description></item>
///   <item><description>Agent <c>my-feature-producer</c> v1 — pins
///   <c>@codeflow/producer-base</c> v1; TODO slot for "what you're producing".</description></item>
///   <item><description>Agent <c>my-feature-reviewer</c> v1 — pins
///   <c>@codeflow/reviewer-base</c> v1; declares Approved + Rejected.</description></item>
///   <item><description>Hitl agent <c>my-feature-escalation-form</c> v1 — passthrough form
///   with Approved + Cancelled ports the operator chooses on round-budget exhaustion.</description></item>
///   <item><description>Workflow <c>my-feature-inner</c> v1 — Start (producer) → reviewer.</description></item>
///   <item><description>Workflow <c>my-feature</c> v1 — Start (setup) → ReviewLoop → on
///   Approved exits cleanly; on Exhausted routes to the HITL escalation.</description></item>
/// </list>
/// </summary>
internal static class SetupLoopFinalizeTemplate
{
    public const string Id = "setup-loop-finalize";

    public static WorkflowTemplate Build() => new(
        Id: Id,
        Name: "Setup → loop → finalize",
        Description: "A setup agent seeds the workflow bag from the input, then a producer/"
            + "reviewer ReviewLoop iterates against that bag. On round-budget exhaustion the "
            + "Exhausted port routes to a human approval gate. Use when the loop body needs "
            + "to read data that has to be parsed once at the start.",
        Category: WorkflowTemplateCategory.Other,
        Materialize: MaterializeAsync,
        PlanKeys: prefix => new[]
        {
            new PlannedEntityKey(MaterializedEntityKind.Agent, $"{prefix}-setup"),
            new PlannedEntityKey(MaterializedEntityKind.Agent, $"{prefix}-producer"),
            new PlannedEntityKey(MaterializedEntityKind.Agent, $"{prefix}-reviewer"),
            new PlannedEntityKey(MaterializedEntityKind.Agent, $"{prefix}-escalation-form"),
            new PlannedEntityKey(MaterializedEntityKind.Workflow, $"{prefix}-inner"),
            new PlannedEntityKey(MaterializedEntityKind.Workflow, prefix),
        });

    private static async Task<MaterializedTemplateResult> MaterializeAsync(
        TemplateMaterializationContext context)
    {
        var prefix = context.NamePrefix;
        var setupKey = $"{prefix}-setup";
        var producerKey = $"{prefix}-producer";
        var reviewerKey = $"{prefix}-reviewer";
        var escalationKey = $"{prefix}-escalation-form";
        var innerKey = $"{prefix}-inner";
        var outerKey = prefix;

        var setupVersion = await context.AgentRepository.CreateNewVersionAsync(
            setupKey, BuildSetupConfigJson(), context.CreatedBy, context.CancellationToken);

        var producerVersion = await context.AgentRepository.CreateNewVersionAsync(
            producerKey, BuildProducerConfigJson(), context.CreatedBy, context.CancellationToken);

        var reviewerVersion = await context.AgentRepository.CreateNewVersionAsync(
            reviewerKey, BuildReviewerConfigJson(), context.CreatedBy, context.CancellationToken);

        var escalationVersion = await context.AgentRepository.CreateNewVersionAsync(
            escalationKey, BuildEscalationFormConfigJson(), context.CreatedBy, context.CancellationToken);

        var innerVersion = await context.WorkflowRepository.CreateNewVersionAsync(
            BuildInnerWorkflow(innerKey, producerKey, producerVersion, reviewerKey, reviewerVersion),
            context.CancellationToken);

        var outerVersion = await context.WorkflowRepository.CreateNewVersionAsync(
            BuildOuterWorkflow(
                outerKey,
                setupKey, setupVersion,
                innerKey, innerVersion,
                escalationKey, escalationVersion),
            context.CancellationToken);

        return new MaterializedTemplateResult(
            EntryWorkflowKey: outerKey,
            EntryWorkflowVersion: outerVersion,
            CreatedEntities: new[]
            {
                new MaterializedEntity(MaterializedEntityKind.Agent, setupKey, setupVersion),
                new MaterializedEntity(MaterializedEntityKind.Agent, producerKey, producerVersion),
                new MaterializedEntity(MaterializedEntityKind.Agent, reviewerKey, reviewerVersion),
                new MaterializedEntity(MaterializedEntityKind.Agent, escalationKey, escalationVersion),
                new MaterializedEntity(MaterializedEntityKind.Workflow, innerKey, innerVersion),
                new MaterializedEntity(MaterializedEntityKind.Workflow, outerKey, outerVersion),
            });
    }

    private static string BuildSetupConfigJson() =>
        """
        {
            "type": "agent",
            "name": "Setup",
            "provider": "openai",
            "model": "gpt-test",
            "systemPrompt": "You are the setup agent for a Setup → loop → finalize workflow. The input script (configured on the workflow node, not on you) seeds the workflow bag from the input artifact. Your only job at runtime is to confirm readiness by writing a one-line summary of what the loop body should expect, then submit on the Continue port.\n\n## CRITICAL OUTPUT RULE\nWrite the readiness summary as your message content BEFORE calling submit.",
            "promptTemplate": "## Initial input\n{{ input }}\n\n## Workflow bag (seeded by input script)\n{{ workflow }}",
            "outputs": [
                { "kind": "Continue", "description": "Setup complete; loop body can run." }
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
            "systemPrompt": "You are the producer in a draft-and-review loop.\n\n{{ include \"@codeflow/producer-base\" }}\n\n## What you are producing\n\nTODO: describe the artifact this producer creates (e.g. \"a markdown summary of the input ticket\", \"a code patch that addresses the request\", \"a structured plan with phases and tasks\"). Replace this section with concrete instructions before running.\n\n## Inputs you receive\n\n- `{{ input }}` — the request from the upstream setup or prior round.\n- `{{ rejectionHistory }}` — accumulated reviewer feedback from prior rounds (empty on round 1).\n\nProduce the artifact, then submit on the Continue port.",
            "promptTemplate": "## Workflow bag\n{{ workflow }}\n\n## Request\n{{ input }}\n\n{{ if rejectionHistory }}## Prior reviewer feedback (do NOT re-do work the reviewer has already accepted)\n{{ rejectionHistory }}\n{{ end }}",
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
            "systemPrompt": "You are the reviewer in a draft-and-review loop.\n\n{{ include \"@codeflow/reviewer-base\" }}\n\n## Acceptance criteria\n\nTODO: describe the explicit criteria the producer's artifact must satisfy. Replace this section with a checklist before running.\n\n## Decision\n\n- Submit `Approved` when the artifact meets the criteria. Write a brief approval rationale as your message content.\n- Submit `Rejected` with concrete, actionable feedback the producer can address in the next round.",
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

    private static string BuildEscalationFormConfigJson() =>
        """
        {
            "type": "hitl",
            "name": "Round-budget exhaustion escalation",
            "description": "The producer/reviewer loop hit the round budget without approval. Review the producer's most recent artifact (shown as the form input) and decide whether to approve it as-is or cancel the workflow.",
            "outputTemplate": "{{ input }}",
            "outputs": [
                { "kind": "Approved", "description": "Operator approves the artifact despite the loop exhausting; passes through unchanged." },
                { "kind": "Cancelled", "description": "Operator cancels; downstream consumers should treat this as an explicit abort.", "contentOptional": true }
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
        string outerKey,
        string setupKey, int setupVersion,
        string innerKey, int innerVersion,
        string escalationKey, int escalationVersion)
    {
        var setupNodeId = Guid.NewGuid();
        var loopNodeId = Guid.NewGuid();
        var escalationNodeId = Guid.NewGuid();

        const string setupInputScript =
            "// TODO: seed workflow vars from the input artifact here.\n"
            + "// Example: setWorkflow('requestSummary', input.summary || input.text);\n"
            + "//          setWorkflow('targetRepo', input.repo);\n"
            + "// The setup agent's prompt template references {{ workflow.* }} so anything you\n"
            + "// seed here flows into the loop body via the workflow bag's copy-on-fork.\n";

        return new WorkflowDraft(
            Key: outerKey,
            Name: $"{outerKey} (setup → loop → finalize)",
            MaxRoundsPerRound: 5,
            Nodes: new[]
            {
                new WorkflowNodeDraft(
                    Id: setupNodeId,
                    Kind: WorkflowNodeKind.Start,
                    AgentKey: setupKey,
                    AgentVersion: setupVersion,
                    OutputScript: null,
                    OutputPorts: new[] { "Continue" },
                    LayoutX: 0, LayoutY: 0,
                    InputScript: setupInputScript),
                new WorkflowNodeDraft(
                    Id: loopNodeId,
                    Kind: WorkflowNodeKind.ReviewLoop,
                    AgentKey: null,
                    AgentVersion: null,
                    OutputScript: null,
                    // ReviewLoop synthesizes its `Exhausted` port at runtime; declaring it
                    // alongside the loopDecision-derived port (`Approved` here) trips
                    // WorkflowValidator's reserved-port rule on every subsequent edit.
                    OutputPorts: new[] { "Approved" },
                    LayoutX: 250, LayoutY: 0,
                    SubflowKey: innerKey,
                    SubflowVersion: innerVersion,
                    ReviewMaxRounds: 5,
                    LoopDecision: "Rejected",
                    RejectionHistory: new RejectionHistoryConfig(Enabled: true)),
                new WorkflowNodeDraft(
                    Id: escalationNodeId,
                    Kind: WorkflowNodeKind.Hitl,
                    AgentKey: escalationKey,
                    AgentVersion: escalationVersion,
                    OutputScript: null,
                    OutputPorts: new[] { "Approved", "Cancelled" },
                    LayoutX: 500, LayoutY: 100),
            },
            Edges: new[]
            {
                new WorkflowEdgeDraft(
                    FromNodeId: setupNodeId,
                    FromPort: "Continue",
                    ToNodeId: loopNodeId,
                    ToPort: WorkflowEdge.DefaultInputPort,
                    RotatesRound: false,
                    SortOrder: 0),
                new WorkflowEdgeDraft(
                    FromNodeId: loopNodeId,
                    FromPort: "Exhausted",
                    ToNodeId: escalationNodeId,
                    ToPort: WorkflowEdge.DefaultInputPort,
                    RotatesRound: false,
                    SortOrder: 1),
            },
            Inputs: Array.Empty<WorkflowInputDraft>());
    }
}
