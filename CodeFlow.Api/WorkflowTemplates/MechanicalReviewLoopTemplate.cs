using CodeFlow.Persistence;

namespace CodeFlow.Api.WorkflowTemplates;

/// <summary>
/// sc-273 — scaffold the canonical "mechanical-then-model" review loop in one click. Borrows
/// from Protostar's harness pattern: deterministic checks (lint / build / test) gate the
/// expensive model reviewer, so a failing build never spends reviewer tokens. Both gates feed
/// the same repair loop, so the developer agent sees rejection feedback regardless of which
/// gate caught the issue.
///
/// For prefix <c>my-feature</c>, materializes six entities:
/// <list type="bullet">
///   <item><description>Agent <c>my-feature-trigger</c> v1 — kickoff agent the outer Start
///   invokes, forwards input to the loop body verbatim.</description></item>
///   <item><description>Agent <c>my-feature-developer</c> v1 — pins
///   <c>@codeflow/producer-base</c>; the agent that drafts the artifact and addresses
///   rejection feedback on subsequent rounds.</description></item>
///   <item><description>Agent <c>my-feature-mechanical-gate</c> v1 — runs deterministic
///   checks via host tools. Assigned the seeded <c>code-worker</c> role at materialization
///   time so it has <c>run_command</c> + <c>read_file</c> grants. System prompt has a
///   "checks to run" slot the operator fills in (e.g. <c>npm test</c>, <c>cargo build</c>,
///   <c>dotnet build</c>). Decides <c>Approved</c> / <c>Rejected</c>.</description></item>
///   <item><description>Agent <c>my-feature-reviewer</c> v1 — pins
///   <c>@codeflow/reviewer-base</c>; LLM-side semantic review. Decides
///   <c>Approved</c> / <c>Rejected</c>.</description></item>
///   <item><description>Workflow <c>my-feature-inner</c> v1 — Start (developer) →
///   mechanical-gate. On mechanical <c>Approved</c> → reviewer. On mechanical <c>Rejected</c>
///   the subflow exits early via the inherited terminal port, skipping the model reviewer
///   entirely (the token-savings win). Reviewer's Approved / Rejected are the other terminal
///   ports.</description></item>
///   <item><description>Workflow <c>my-feature</c> v1 — Start (trigger) → ReviewLoop pointing
///   at <c>my-feature-inner</c>, configured with <c>loopDecision = "Rejected"</c>,
///   <c>reviewMaxRounds = 5</c>, and <c>rejectionHistory.Enabled = true</c>. Both
///   mechanical and model rejections feed back into the developer's
///   <c>{{ rejectionHistory }}</c> on the next round.</description></item>
/// </list>
/// </summary>
internal static class MechanicalReviewLoopTemplate
{
    public const string Id = "mechanical-review-loop";

    public static WorkflowTemplate Build() => new(
        Id: Id,
        Name: "Mechanical-then-model review loop",
        Description: "A developer agent paired with a deterministic mechanical gate (lint / "
            + "build / test) and a model-side semantic reviewer, wired into a bounded repair "
            + "loop. Failing checks short-circuit the model reviewer entirely so a broken "
            + "build never spends reviewer tokens. Both rejections feed the same repair loop "
            + "so the developer sees structured feedback regardless of which gate caught the "
            + "issue. The mechanical-gate agent is auto-assigned the seeded 'code-worker' "
            + "role for run_command + read_file access; the reviewer is LLM-only.",
        Category: WorkflowTemplateCategory.ReviewLoop,
        Materialize: MaterializeAsync,
        PlanKeys: prefix => new[]
        {
            new PlannedEntityKey(MaterializedEntityKind.Agent, $"{prefix}-trigger"),
            new PlannedEntityKey(MaterializedEntityKind.Agent, $"{prefix}-developer"),
            new PlannedEntityKey(MaterializedEntityKind.Agent, $"{prefix}-mechanical-gate"),
            new PlannedEntityKey(MaterializedEntityKind.Agent, $"{prefix}-reviewer"),
            new PlannedEntityKey(MaterializedEntityKind.Workflow, $"{prefix}-inner"),
            new PlannedEntityKey(MaterializedEntityKind.Workflow, prefix),
        });

    private static async Task<MaterializedTemplateResult> MaterializeAsync(
        TemplateMaterializationContext context)
    {
        var prefix = context.NamePrefix;
        var triggerKey = $"{prefix}-trigger";
        var developerKey = $"{prefix}-developer";
        var mechanicalGateKey = $"{prefix}-mechanical-gate";
        var reviewerKey = $"{prefix}-reviewer";
        var innerKey = $"{prefix}-inner";
        var outerKey = prefix;

        var triggerVersion = await context.AgentRepository.CreateNewVersionAsync(
            triggerKey, BuildTriggerConfigJson(), context.CreatedBy, context.CancellationToken);

        var developerVersion = await context.AgentRepository.CreateNewVersionAsync(
            developerKey, BuildDeveloperConfigJson(), context.CreatedBy, context.CancellationToken);

        var mechanicalGateVersion = await context.AgentRepository.CreateNewVersionAsync(
            mechanicalGateKey, BuildMechanicalGateConfigJson(), context.CreatedBy, context.CancellationToken);

        var reviewerVersion = await context.AgentRepository.CreateNewVersionAsync(
            reviewerKey, BuildReviewerConfigJson(), context.CreatedBy, context.CancellationToken);

        // Auto-assign the seeded code-worker role to the mechanical-gate agent so it has
        // run_command + read_file out of the box. If the operator's environment doesn't have
        // the seeded role (e.g. a custom seed), they can swap in an equivalent role
        // post-materialization. We don't fail the materialization on a missing role —
        // mechanical gate without grants would still validate; it just wouldn't have shell
        // access until the operator wires one up.
        var codeWorkerRole = await context.RoleRepository.GetByKeyAsync(
            SystemAgentRoles.CodeWorkerKey, context.CancellationToken);
        if (codeWorkerRole is not null)
        {
            await context.RoleRepository.ReplaceAssignmentsAsync(
                mechanicalGateKey,
                new[] { codeWorkerRole.Id },
                context.CancellationToken);
        }

        var innerVersion = await context.WorkflowRepository.CreateNewVersionAsync(
            BuildInnerWorkflow(
                innerKey,
                developerKey, developerVersion,
                mechanicalGateKey, mechanicalGateVersion,
                reviewerKey, reviewerVersion),
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
                new MaterializedEntity(MaterializedEntityKind.Agent, developerKey, developerVersion),
                new MaterializedEntity(MaterializedEntityKind.Agent, mechanicalGateKey, mechanicalGateVersion),
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
            "systemPrompt": "You are the kickoff agent for a mechanical-then-model review loop. Your only job is to forward the workflow input to the body of the loop verbatim. Write the input back as your message content, then submit on the Continue port.",
            "promptTemplate": "{{ input }}",
            "outputs": [
                { "kind": "Continue" }
            ]
        }
        """;

    private static string BuildDeveloperConfigJson() =>
        """
        {
            "type": "agent",
            "name": "Developer",
            "provider": "openai",
            "model": "gpt-test",
            "systemPrompt": "You are the developer in a mechanical-then-model review loop.\n\n{{ include \"@codeflow/producer-base\" }}\n\n## What you are producing\n\nTODO: describe the artifact this developer creates (e.g. \"a code patch that addresses the request\", \"a refactor that extracts the duplicated handler\", \"a fix for the failing test\"). Replace this section with concrete instructions before running.\n\n## Inputs you receive\n\n- `{{ input }}` — the request from the outer trigger.\n- `{{ rejectionHistory }}` — accumulated feedback from prior rounds. Mechanical-gate failures (build / lint / test errors) and reviewer feedback both land here, marked by which gate produced them.\n\nProduce the artifact, then submit on the Continue port. The mechanical gate runs next.",
            "promptTemplate": "## Request\n{{ input }}\n\n{{ if rejectionHistory }}## Prior round feedback (do NOT re-do work that prior rounds got right)\n{{ rejectionHistory }}\n{{ end }}",
            "partialPins": [
                { "key": "@codeflow/producer-base", "version": 1 }
            ],
            "outputs": [
                { "kind": "Continue" }
            ]
        }
        """;

    private static string BuildMechanicalGateConfigJson() =>
        """
        {
            "type": "agent",
            "name": "Mechanical gate",
            "provider": "openai",
            "model": "gpt-test",
            "systemPrompt": "You are a deterministic mechanical gate. You do NOT make subjective quality calls — you ONLY run the configured checks and report the results.\n\n## Checks to run\n\nTODO: list the exact commands to run, in order. Examples:\n- `dotnet build CodeFlow.Api/CodeFlow.Api.csproj`\n- `dotnet test CodeFlow.Api.Tests/CodeFlow.Api.Tests.csproj`\n- `npm run lint --prefix codeflow-ui`\n- `npm run typecheck --prefix codeflow-ui`\n\nReplace this section with the concrete check set before running.\n\n## How to decide\n\n- Run each check via `run_command`. Capture the exit code and the last 40 lines of output.\n- If EVERY check exits 0, submit `Approved` with a one-line summary of which checks ran.\n- If ANY check fails, submit `Rejected` with the failing command + the last 40 lines of its output. Do NOT attempt to fix the failure — that is the developer's job on the next round.\n\nA `Rejected` here short-circuits the model reviewer entirely (no reviewer tokens spent), so the message you write IS the rejection feedback the developer sees on round N+1. Be specific.",
            "promptTemplate": "## Developer's submission (round {{ round }} of {{ maxRounds }})\n{{ input }}",
            "outputs": [
                { "kind": "Approved" },
                { "kind": "Rejected" }
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
            "systemPrompt": "You are the model-side semantic reviewer in a mechanical-then-model review loop. The mechanical gate (lint / build / test) has ALREADY passed for the submission you are reviewing — focus your review on what the deterministic checks cannot evaluate.\n\n{{ include \"@codeflow/reviewer-base\" }}\n\n## Acceptance criteria (semantic)\n\nTODO: describe the criteria that require human or model judgment. Examples:\n- \"The change addresses the requested behavior, not just the cited symptom.\"\n- \"Public API surface follows the conventions in {{ workflow.styleGuide }}.\"\n- \"Error handling distinguishes user errors from operational ones; no silent catches.\"\n- \"New tests cover the previously-uncovered branch identified in the request.\"\n\nReplace this section with concrete criteria before running.\n\n## Decision\n\n- Submit `Approved` when the artifact meets the criteria. Write a brief approval rationale.\n- Submit `Rejected` with concrete, actionable feedback. Mechanical-style nits (\"missing semicolon\") belong to the mechanical gate, not here — focus on intent and quality.",
            "promptTemplate": "## Developer's submission (round {{ round }} of {{ maxRounds }})\n{{ input }}",
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
        string innerKey,
        string developerKey, int developerVersion,
        string mechanicalGateKey, int mechanicalGateVersion,
        string reviewerKey, int reviewerVersion)
    {
        var startNodeId = Guid.NewGuid();
        var mechanicalGateNodeId = Guid.NewGuid();
        var reviewerNodeId = Guid.NewGuid();

        return new WorkflowDraft(
            Key: innerKey,
            Name: $"{innerKey} (mechanical-then-model gate)",
            MaxRoundsPerRound: 5,
            Nodes: new[]
            {
                new WorkflowNodeDraft(
                    Id: startNodeId,
                    Kind: WorkflowNodeKind.Start,
                    AgentKey: developerKey,
                    AgentVersion: developerVersion,
                    OutputScript: null,
                    OutputPorts: new[] { "Continue", "Failed" },
                    LayoutX: 0, LayoutY: 0),
                new WorkflowNodeDraft(
                    Id: mechanicalGateNodeId,
                    Kind: WorkflowNodeKind.Agent,
                    AgentKey: mechanicalGateKey,
                    AgentVersion: mechanicalGateVersion,
                    OutputScript: null,
                    // Approved → reviewer (model gate). Rejected has NO outgoing edge, so the
                    // subflow terminates on this port and the parent ReviewLoop sees a
                    // "Rejected" decision (loopDecision = "Rejected" → loop). This is the
                    // mechanical-then-model short-circuit.
                    OutputPorts: new[] { "Approved", "Rejected", "Failed" },
                    LayoutX: 250, LayoutY: 0),
                new WorkflowNodeDraft(
                    Id: reviewerNodeId,
                    Kind: WorkflowNodeKind.Agent,
                    AgentKey: reviewerKey,
                    AgentVersion: reviewerVersion,
                    OutputScript: null,
                    OutputPorts: new[] { "Approved", "Rejected", "Failed" },
                    LayoutX: 500, LayoutY: 0),
            },
            Edges: new[]
            {
                new WorkflowEdgeDraft(
                    FromNodeId: startNodeId,
                    FromPort: "Continue",
                    ToNodeId: mechanicalGateNodeId,
                    ToPort: WorkflowEdge.DefaultInputPort,
                    RotatesRound: false,
                    SortOrder: 0),
                // Approved on the mechanical gate routes to the reviewer. Rejected on the
                // mechanical gate is intentionally unwired — the subflow exits with the
                // mechanical Rejected decision so the outer ReviewLoop reroutes immediately.
                new WorkflowEdgeDraft(
                    FromNodeId: mechanicalGateNodeId,
                    FromPort: "Approved",
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
            Name: $"{outerKey} (mechanical-then-model review loop)",
            MaxRoundsPerRound: 5,
            Nodes: new[]
            {
                new WorkflowNodeDraft(
                    Id: startNodeId,
                    Kind: WorkflowNodeKind.Start,
                    AgentKey: triggerKey,
                    AgentVersion: triggerVersion,
                    OutputScript: null,
                    OutputPorts: new[] { "Continue", "Failed" },
                    LayoutX: 0, LayoutY: 0),
                new WorkflowNodeDraft(
                    Id: loopNodeId,
                    Kind: WorkflowNodeKind.ReviewLoop,
                    AgentKey: null,
                    AgentVersion: null,
                    OutputScript: null,
                    OutputPorts: new[] { "Approved", "Exhausted", "Failed" },
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
