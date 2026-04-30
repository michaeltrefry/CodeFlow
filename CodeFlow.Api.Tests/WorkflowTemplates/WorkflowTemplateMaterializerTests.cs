using CodeFlow.Api.WorkflowTemplates;
using CodeFlow.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MariaDb;

namespace CodeFlow.Api.Tests.WorkflowTemplates;

/// <summary>
/// Materializer integration coverage. Uses Testcontainers MariaDB (not in-memory EF) because
/// the underlying repositories rely on real transactions for atomic save sequences.
/// </summary>
[Trait("Category", "EndToEnd")]
public sealed class WorkflowTemplateMaterializerTests : IAsyncLifetime
{
    private readonly MariaDbContainer mariaDbContainer = new MariaDbBuilder("mariadb:11.4")
        .WithDatabase("codeflow_template_tests")
        .WithUsername("codeflow")
        .WithPassword("codeflow_dev")
        .Build();

    private string? connectionString;

    public async Task InitializeAsync()
    {
        await mariaDbContainer.StartAsync();
        connectionString = mariaDbContainer.GetConnectionString();

        await using var ctx = CreateDbContext();
        await ctx.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await mariaDbContainer.DisposeAsync();
    }

    [Fact]
    public async Task MaterializeAsync_EmptyWorkflowTemplate_CreatesAgentAndWorkflow()
    {
        // S3: the EmptyWorkflowTemplate stub creates one agent + one workflow with the prefix
        // baked into both keys, returns both in the result so the editor can navigate.
        AgentConfigRepository.ClearCacheForTests();
        var materializer = CreateMaterializer();

        var result = await materializer.MaterializeAsync(
            templateId: WorkflowTemplateRegistry.EmptyWorkflowId,
            namePrefix: "demo",
            createdBy: "tester");

        result.EntryWorkflowKey.Should().Be("demo");
        result.EntryWorkflowVersion.Should().Be(1);
        result.CreatedEntities.Should().HaveCount(2);
        result.CreatedEntities.Should().Contain(e =>
            e.Kind == MaterializedEntityKind.Agent && e.Key == "demo-start" && e.Version == 1);
        result.CreatedEntities.Should().Contain(e =>
            e.Kind == MaterializedEntityKind.Workflow && e.Key == "demo" && e.Version == 1);

        await using var verifyCtx = CreateDbContext();
        var agentRepo = new AgentConfigRepository(verifyCtx);
        var agent = await agentRepo.GetAsync("demo-start", 1);
        agent.Configuration.Provider.Should().Be("openai");
        agent.Configuration.SystemPrompt.Should().Contain("Replace this prompt");

        var workflowRepo = new WorkflowRepository(verifyCtx);
        var workflow = await workflowRepo.GetAsync("demo", 1);
        workflow.Nodes.Should().ContainSingle();
        workflow.Nodes[0].Kind.Should().Be(WorkflowNodeKind.Start);
        workflow.Nodes[0].AgentKey.Should().Be("demo-start");
        workflow.Nodes[0].AgentVersion.Should().Be(1);
    }

    [Fact]
    public async Task MaterializeAsync_UnknownTemplate_ThrowsTemplateNotFound()
    {
        AgentConfigRepository.ClearCacheForTests();
        var materializer = CreateMaterializer();

        var act = () => materializer.MaterializeAsync(
            templateId: "ghost-template",
            namePrefix: "demo",
            createdBy: null);

        await act.Should().ThrowAsync<WorkflowTemplateNotFoundException>()
            .Where(ex => ex.TemplateId == "ghost-template");
    }

    [Fact]
    public async Task MaterializeAsync_BlankPrefix_ThrowsArgumentException()
    {
        AgentConfigRepository.ClearCacheForTests();
        var materializer = CreateMaterializer();

        var act = () => materializer.MaterializeAsync(
            templateId: WorkflowTemplateRegistry.EmptyWorkflowId,
            namePrefix: "   ",
            createdBy: null);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("dot.notation")]
    [InlineData("slash/path")]
    [InlineData("colon:scoped")]
    public async Task MaterializeAsync_PrefixWithIllegalChar_ThrowsArgumentException(string prefix)
    {
        // Letters / digits / hyphens / underscores are the only legal prefix characters —
        // anything else risks colliding with reserved separators in workflow / agent keys.
        AgentConfigRepository.ClearCacheForTests();
        var materializer = CreateMaterializer();

        var act = () => materializer.MaterializeAsync(
            templateId: WorkflowTemplateRegistry.EmptyWorkflowId,
            namePrefix: prefix,
            createdBy: null);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task MaterializeAsync_PrefixCollidesWithExisting_ThrowsKeyCollisionException()
    {
        // S3: materializing twice with the same prefix must surface as a typed collision —
        // the AgentConfigRepository.CreateNewVersionAsync would otherwise quietly create v2
        // of the existing agent and the operator would have a half-merged template.
        AgentConfigRepository.ClearCacheForTests();
        var prefix = $"collide-{Guid.NewGuid():N}";

        var firstMaterializer = CreateMaterializer();
        await firstMaterializer.MaterializeAsync(
            templateId: WorkflowTemplateRegistry.EmptyWorkflowId,
            namePrefix: prefix,
            createdBy: null);

        var secondMaterializer = CreateMaterializer();
        var act = () => secondMaterializer.MaterializeAsync(
            templateId: WorkflowTemplateRegistry.EmptyWorkflowId,
            namePrefix: prefix,
            createdBy: null);

        var ex = (await act.Should().ThrowAsync<TemplateKeyCollisionException>()).Which;
        ex.Conflicts.Should().HaveCount(2);
        ex.Conflicts.Select(c => c.Key).Should().Contain(prefix);
        ex.Conflicts.Select(c => c.Key).Should().Contain($"{prefix}-start");
    }

    [Fact]
    public async Task MaterializeAsync_PrefixIsTrimmed()
    {
        // Operator-supplied whitespace shouldn't bleed into the persisted keys.
        AgentConfigRepository.ClearCacheForTests();
        var materializer = CreateMaterializer();

        var result = await materializer.MaterializeAsync(
            templateId: WorkflowTemplateRegistry.EmptyWorkflowId,
            namePrefix: "   demo-trimmed   ",
            createdBy: null);

        result.EntryWorkflowKey.Should().Be("demo-trimmed");
    }

    [Fact]
    public void TemplateRegistry_ListsBundledTemplatesByDefault()
    {
        var registry = new WorkflowTemplateRegistry();

        var listed = registry.List();

        listed.Should().NotBeEmpty();
        listed.Should().Contain(t => t.Id == WorkflowTemplateRegistry.EmptyWorkflowId
            && t.Category == WorkflowTemplateCategory.Empty);
        listed.Should().Contain(t => t.Id == "review-loop-pair"
            && t.Category == WorkflowTemplateCategory.ReviewLoop);
        // sc-273 — mechanical-then-model review loop ships under ReviewLoop category alongside
        // review-loop-pair so both surface together in the picker.
        listed.Should().Contain(t => t.Id == "mechanical-review-loop"
            && t.Category == WorkflowTemplateCategory.ReviewLoop);
        listed.Should().Contain(t => t.Id == "hitl-approval-gate"
            && t.Category == WorkflowTemplateCategory.Hitl);
        listed.Should().Contain(t => t.Id == "setup-loop-finalize"
            && t.Category == WorkflowTemplateCategory.Other);
        listed.Should().Contain(t => t.Id == "lifecycle-wrapper"
            && t.Category == WorkflowTemplateCategory.Lifecycle);
    }

    [Fact]
    public async Task LifecycleWrapperTemplate_Materialize_CreatesAllExpectedEntities()
    {
        // S7 acceptance: 3 phases + 2 gates → 8 entities (1 trigger + 1 phase trigger +
        // 3 phase workflows + 2 gate forms + 1 lifecycle workflow).
        AgentConfigRepository.ClearCacheForTests();
        var prefix = $"life-{Guid.NewGuid():N}";
        var materializer = CreateMaterializer();

        var result = await materializer.MaterializeAsync(
            templateId: "lifecycle-wrapper",
            namePrefix: prefix,
            createdBy: "tester");

        result.EntryWorkflowKey.Should().Be(prefix);
        result.EntryWorkflowVersion.Should().Be(1);
        result.CreatedEntities.Should().HaveCount(8);
        result.CreatedEntities.Select(e => e.Key).Should().BeEquivalentTo(new[]
        {
            $"{prefix}-trigger",
            $"{prefix}-phase-trigger",
            $"{prefix}-phase-1",
            $"{prefix}-phase-2",
            $"{prefix}-phase-3",
            $"{prefix}-gate-1-form",
            $"{prefix}-gate-2-form",
            prefix,
        });
    }

    [Fact]
    public async Task LifecycleWrapperTemplate_OuterWorkflow_ChainsPhasesThroughGates()
    {
        // S7 acceptance: the lifecycle wires Start → phase-1 → gate-1 → phase-2 → gate-2 →
        // phase-3 (terminal). Each gate's Approved port routes to the next phase; Cancelled
        // is unwired (terminal exit / abort).
        AgentConfigRepository.ClearCacheForTests();
        var prefix = $"life-wiring-{Guid.NewGuid():N}";
        var materializer = CreateMaterializer();

        await materializer.MaterializeAsync(
            templateId: "lifecycle-wrapper",
            namePrefix: prefix,
            createdBy: null);

        await using var verifyCtx = CreateDbContext();
        var workflowRepo = new WorkflowRepository(verifyCtx);

        var lifecycle = await workflowRepo.GetAsync(prefix, 1);
        var subflowNodes = lifecycle.Nodes.Where(n => n.Kind == WorkflowNodeKind.Subflow)
            .OrderBy(n => n.LayoutX)
            .ToArray();
        var hitlNodes = lifecycle.Nodes.Where(n => n.Kind == WorkflowNodeKind.Hitl)
            .OrderBy(n => n.LayoutX)
            .ToArray();

        subflowNodes.Should().HaveCount(3);
        hitlNodes.Should().HaveCount(2);

        subflowNodes[0].SubflowKey.Should().Be($"{prefix}-phase-1");
        subflowNodes[1].SubflowKey.Should().Be($"{prefix}-phase-2");
        subflowNodes[2].SubflowKey.Should().Be($"{prefix}-phase-3");

        // phase-1 → gate-1 → phase-2 → gate-2 → phase-3.
        var phase1ToGate1 = lifecycle.Edges.Single(e =>
            e.FromNodeId == subflowNodes[0].Id && e.FromPort == "Completed");
        phase1ToGate1.ToNodeId.Should().Be(hitlNodes[0].Id);

        var gate1Approved = lifecycle.Edges.Single(e =>
            e.FromNodeId == hitlNodes[0].Id && e.FromPort == "Approved");
        gate1Approved.ToNodeId.Should().Be(subflowNodes[1].Id);

        var phase2ToGate2 = lifecycle.Edges.Single(e =>
            e.FromNodeId == subflowNodes[1].Id && e.FromPort == "Completed");
        phase2ToGate2.ToNodeId.Should().Be(hitlNodes[1].Id);

        var gate2Approved = lifecycle.Edges.Single(e =>
            e.FromNodeId == hitlNodes[1].Id && e.FromPort == "Approved");
        gate2Approved.ToNodeId.Should().Be(subflowNodes[2].Id);

        // Phase-3's Completed port is unwired → terminal.
        lifecycle.Edges.Should().NotContain(e =>
            e.FromNodeId == subflowNodes[2].Id && e.FromPort == "Completed");
    }

    [Fact]
    public async Task SetupLoopFinalizeTemplate_Materialize_CreatesAllSixEntities()
    {
        // S6 acceptance: scaffold lands setup + producer + reviewer + escalation HITL +
        // inner workflow + outer workflow. Authors fill in the setup input-script TODO and
        // the producer/reviewer prompts to operationalize it.
        AgentConfigRepository.ClearCacheForTests();
        var prefix = $"slf-{Guid.NewGuid():N}";
        var materializer = CreateMaterializer();

        var result = await materializer.MaterializeAsync(
            templateId: "setup-loop-finalize",
            namePrefix: prefix,
            createdBy: "tester");

        result.EntryWorkflowKey.Should().Be(prefix);
        result.EntryWorkflowVersion.Should().Be(1);
        result.CreatedEntities.Should().HaveCount(6);
        result.CreatedEntities.Select(e => e.Key).Should().BeEquivalentTo(new[]
        {
            $"{prefix}-setup",
            $"{prefix}-producer",
            $"{prefix}-reviewer",
            $"{prefix}-escalation-form",
            $"{prefix}-inner",
            prefix,
        });
    }

    [Fact]
    public async Task SetupLoopFinalizeTemplate_OuterWorkflow_WiresExhaustedToHitlEscalation()
    {
        // S6 acceptance: the outer workflow's ReviewLoop routes its Exhausted port to the
        // HITL escalation form, while Approved exits cleanly as a terminal port.
        AgentConfigRepository.ClearCacheForTests();
        var prefix = $"slf-wiring-{Guid.NewGuid():N}";
        var materializer = CreateMaterializer();

        await materializer.MaterializeAsync(
            templateId: "setup-loop-finalize",
            namePrefix: prefix,
            createdBy: null);

        await using var verifyCtx = CreateDbContext();
        var workflowRepo = new WorkflowRepository(verifyCtx);

        var outer = await workflowRepo.GetAsync(prefix, 1);
        var loopNode = outer.Nodes.Single(n => n.Kind == WorkflowNodeKind.ReviewLoop);
        var hitlNode = outer.Nodes.Single(n => n.Kind == WorkflowNodeKind.Hitl);

        loopNode.SubflowKey.Should().Be($"{prefix}-inner");
        loopNode.LoopDecision.Should().Be("Rejected");
        loopNode.RejectionHistory.Should().NotBeNull();
        loopNode.RejectionHistory!.Enabled.Should().BeTrue();

        var exhaustedEdge = outer.Edges.Single(e =>
            e.FromNodeId == loopNode.Id && e.FromPort == "Exhausted");
        exhaustedEdge.ToNodeId.Should().Be(hitlNode.Id);

        // Approved port is unwired → terminal → surfaces on the parent Subflow if used.
        outer.TerminalPorts.Should().Contain("Approved");
    }

    [Fact]
    public async Task SetupLoopFinalizeTemplate_SetupNode_HasInputScriptWithTodoComment()
    {
        // S6 acceptance: the setup agent's input-script slot has a TODO comment block
        // explaining where to seed globals. Author fills in the template before running.
        AgentConfigRepository.ClearCacheForTests();
        var prefix = $"slf-todo-{Guid.NewGuid():N}";
        var materializer = CreateMaterializer();

        await materializer.MaterializeAsync(
            templateId: "setup-loop-finalize",
            namePrefix: prefix,
            createdBy: null);

        await using var verifyCtx = CreateDbContext();
        var workflowRepo = new WorkflowRepository(verifyCtx);

        var outer = await workflowRepo.GetAsync(prefix, 1);
        var setupNode = outer.Nodes.Single(n => n.Kind == WorkflowNodeKind.Start);
        setupNode.InputScript.Should().NotBeNullOrEmpty();
        setupNode.InputScript!.Should().Contain("TODO");
        setupNode.InputScript.Should().Contain("setWorkflow");
    }

    [Fact]
    public async Task HitlApprovalGateTemplate_Materialize_CreatesAllThreeEntities()
    {
        // S5 acceptance: scaffold lands trigger + Hitl form + outer workflow with the
        // passthrough outputTemplate and Approved/Cancelled ports. Author drops the resulting
        // workflow as a Subflow node anywhere they want a human checkpoint.
        AgentConfigRepository.ClearCacheForTests();
        var prefix = $"hitl-{Guid.NewGuid():N}";
        var materializer = CreateMaterializer();

        var result = await materializer.MaterializeAsync(
            templateId: "hitl-approval-gate",
            namePrefix: prefix,
            createdBy: "tester");

        result.EntryWorkflowKey.Should().Be(prefix);
        result.EntryWorkflowVersion.Should().Be(1);
        result.CreatedEntities.Should().HaveCount(3);
        result.CreatedEntities.Select(e => e.Key).Should().BeEquivalentTo(new[]
        {
            $"{prefix}-trigger",
            $"{prefix}-form",
            prefix,
        });
    }

    [Fact]
    public async Task HitlApprovalGateTemplate_Materialize_HitlFormHasPassthroughOutputAndExpectedPorts()
    {
        // S5 contract: the HITL agent has outputTemplate "{{ input }}" (passthrough) and
        // declares Approved + Cancelled ports. The workflow's Hitl node wires both ports
        // as terminals (no outgoing edges) so they surface on the parent Subflow node.
        AgentConfigRepository.ClearCacheForTests();
        var prefix = $"hitl-cfg-{Guid.NewGuid():N}";
        var materializer = CreateMaterializer();

        await materializer.MaterializeAsync(
            templateId: "hitl-approval-gate",
            namePrefix: prefix,
            createdBy: null);

        await using var verifyCtx = CreateDbContext();
        var workflowRepo = new WorkflowRepository(verifyCtx);

        var workflow = await workflowRepo.GetAsync(prefix, 1);
        var hitlNode = workflow.Nodes.SingleOrDefault(n => n.Kind == WorkflowNodeKind.Hitl);
        hitlNode.Should().NotBeNull();
        hitlNode!.AgentKey.Should().Be($"{prefix}-form");
        hitlNode.OutputPorts.Should().BeEquivalentTo(new[] { "Approved", "Cancelled" });

        // Both ports are terminals (no outgoing edges) — Subflow consumers see them.
        workflow.TerminalPorts.Should().Contain("Approved");
        // Cancelled is filtered from TerminalPorts in the same way Failed is — but the port
        // is on the node so a Subflow consumer can wire it. Just verify it's declared.
        hitlNode.OutputPorts.Should().Contain("Cancelled");

        var startNode = workflow.Nodes.Single(n => n.Kind == WorkflowNodeKind.Start);
        startNode.AgentKey.Should().Be($"{prefix}-trigger");
        workflow.Edges.Should().ContainSingle();
        workflow.Edges[0].FromPort.Should().Be("Continue");
        workflow.Edges[0].ToNodeId.Should().Be(hitlNode.Id);
    }

    [Fact]
    public async Task ReviewLoopPairTemplate_Materialize_CreatesAllFiveEntities()
    {
        // S4 acceptance: scaffold lands trigger + producer + reviewer agents and inner +
        // outer workflows, all stitched up. Authors run the outer workflow with their input
        // and the loop iterates against the producer/reviewer pair without further wiring.
        AgentConfigRepository.ClearCacheForTests();
        var prefix = $"rlp-{Guid.NewGuid():N}";
        var materializer = CreateMaterializer();

        var result = await materializer.MaterializeAsync(
            templateId: "review-loop-pair",
            namePrefix: prefix,
            createdBy: "tester");

        result.EntryWorkflowKey.Should().Be(prefix);
        result.EntryWorkflowVersion.Should().Be(1);
        result.CreatedEntities.Should().HaveCount(5);
        result.CreatedEntities.Select(e => e.Key).Should().BeEquivalentTo(new[]
        {
            $"{prefix}-trigger",
            $"{prefix}-producer",
            $"{prefix}-reviewer",
            $"{prefix}-inner",
            prefix,
        });
    }

    [Fact]
    public async Task ReviewLoopPairTemplate_Materialize_OuterWorkflowConfiguresReviewLoopWithRejectionHistory()
    {
        // S4 acceptance: ReviewLoop node carries loopDecision=Rejected, maxRounds=5, and
        // rejection-history enabled (P3 feature) so the producer/reviewer get
        // {{ rejectionHistory }} populated from round 2 onward.
        AgentConfigRepository.ClearCacheForTests();
        var prefix = $"rlp-cfg-{Guid.NewGuid():N}";
        var materializer = CreateMaterializer();

        await materializer.MaterializeAsync(
            templateId: "review-loop-pair",
            namePrefix: prefix,
            createdBy: null);

        await using var verifyCtx = CreateDbContext();
        var workflowRepo = new WorkflowRepository(verifyCtx);

        var outer = await workflowRepo.GetAsync(prefix, 1);
        var loopNode = outer.Nodes.SingleOrDefault(n => n.Kind == WorkflowNodeKind.ReviewLoop);
        loopNode.Should().NotBeNull();
        loopNode!.SubflowKey.Should().Be($"{prefix}-inner");
        loopNode.SubflowVersion.Should().Be(1);
        loopNode.ReviewMaxRounds.Should().Be(5);
        loopNode.LoopDecision.Should().Be("Rejected");
        loopNode.RejectionHistory.Should().NotBeNull();
        loopNode.RejectionHistory!.Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task ReviewLoopPairTemplate_Materialize_AgentsPinPartials()
    {
        // S4 acceptance: producer pins @codeflow/producer-base, reviewer pins
        // @codeflow/reviewer-base. The author can swap or extend the pinned partials by
        // editing the agent without breaking the include directive.
        AgentConfigRepository.ClearCacheForTests();
        var prefix = $"rlp-pin-{Guid.NewGuid():N}";
        var materializer = CreateMaterializer();

        await materializer.MaterializeAsync(
            templateId: "review-loop-pair",
            namePrefix: prefix,
            createdBy: null);

        await using var verifyCtx = CreateDbContext();
        var agentRepo = new AgentConfigRepository(verifyCtx);

        var producer = await agentRepo.GetAsync($"{prefix}-producer", 1);
        producer.Configuration.PartialPins.Should().NotBeNull();
        producer.Configuration.PartialPins!.Should().Contain(p =>
            p.Key == "@codeflow/producer-base" && p.Version == 1);

        var reviewer = await agentRepo.GetAsync($"{prefix}-reviewer", 1);
        reviewer.Configuration.PartialPins.Should().NotBeNull();
        reviewer.Configuration.PartialPins!.Should().Contain(p =>
            p.Key == "@codeflow/reviewer-base" && p.Version == 1);
    }

    [Fact]
    public async Task ReviewLoopPairTemplate_Materialize_InnerWorkflowWiresStartToReviewer()
    {
        // S4 acceptance: inner workflow's Start node is the producer (kind=Start), wired by
        // a Continue edge to the reviewer; reviewer declares Approved and Rejected ports so
        // the outer ReviewLoop can iterate.
        AgentConfigRepository.ClearCacheForTests();
        var prefix = $"rlp-inner-{Guid.NewGuid():N}";
        var materializer = CreateMaterializer();

        await materializer.MaterializeAsync(
            templateId: "review-loop-pair",
            namePrefix: prefix,
            createdBy: null);

        await using var verifyCtx = CreateDbContext();
        var workflowRepo = new WorkflowRepository(verifyCtx);

        var inner = await workflowRepo.GetAsync($"{prefix}-inner", 1);
        var startNode = inner.Nodes.Single(n => n.Kind == WorkflowNodeKind.Start);
        startNode.AgentKey.Should().Be($"{prefix}-producer");
        var reviewerNode = inner.Nodes.Single(n => n.Kind == WorkflowNodeKind.Agent);
        reviewerNode.AgentKey.Should().Be($"{prefix}-reviewer");
        reviewerNode.OutputPorts.Should().Contain(new[] { "Approved", "Rejected" });
        inner.Edges.Should().ContainSingle();
        inner.Edges[0].FromNodeId.Should().Be(startNode.Id);
        inner.Edges[0].FromPort.Should().Be("Continue");
        inner.Edges[0].ToNodeId.Should().Be(reviewerNode.Id);
    }

    // ===== sc-273 — MechanicalReviewLoopTemplate ===========================================

    [Fact]
    public async Task MechanicalReviewLoopTemplate_Materialize_CreatesAllSixEntities()
    {
        // sc-273 acceptance: scaffold lands trigger + developer + mechanical-gate + reviewer
        // agents and inner + outer workflows. The mechanical-gate gets the seeded code-worker
        // role assigned automatically so it has run_command + read_file out of the box.
        AgentConfigRepository.ClearCacheForTests();
        await SeedSystemRolesAsync();
        var prefix = $"mech-{Guid.NewGuid():N}";
        var materializer = CreateMaterializer();

        var result = await materializer.MaterializeAsync(
            templateId: "mechanical-review-loop",
            namePrefix: prefix,
            createdBy: "tester");

        result.EntryWorkflowKey.Should().Be(prefix);
        result.EntryWorkflowVersion.Should().Be(1);
        result.CreatedEntities.Should().HaveCount(6);
        result.CreatedEntities.Select(e => e.Key).Should().BeEquivalentTo(new[]
        {
            $"{prefix}-trigger",
            $"{prefix}-developer",
            $"{prefix}-mechanical-gate",
            $"{prefix}-reviewer",
            $"{prefix}-inner",
            prefix,
        });
    }

    [Fact]
    public async Task MechanicalReviewLoopTemplate_InnerWorkflow_ShortCircuitsModelReviewerOnMechanicalRejection()
    {
        // sc-273 core invariant: mechanical Approved routes to the model reviewer; mechanical
        // Rejected has NO outgoing edge — the subflow terminates on that port and the parent
        // ReviewLoop sees a Rejected decision without ever invoking the reviewer agent. This
        // is the token-savings shape the card calls out.
        AgentConfigRepository.ClearCacheForTests();
        await SeedSystemRolesAsync();
        var prefix = $"mech-wiring-{Guid.NewGuid():N}";
        var materializer = CreateMaterializer();

        await materializer.MaterializeAsync(
            templateId: "mechanical-review-loop",
            namePrefix: prefix,
            createdBy: null);

        await using var verifyCtx = CreateDbContext();
        var workflowRepo = new WorkflowRepository(verifyCtx);

        var inner = await workflowRepo.GetAsync($"{prefix}-inner", 1);
        var startNode = inner.Nodes.Single(n => n.Kind == WorkflowNodeKind.Start);
        var agentNodes = inner.Nodes.Where(n => n.Kind == WorkflowNodeKind.Agent)
            .OrderBy(n => n.LayoutX)
            .ToArray();
        agentNodes.Should().HaveCount(2,
            "mechanical-then-model = Start (developer) + mechanical-gate Agent + reviewer Agent");
        var mechanicalGateNode = agentNodes[0];
        var reviewerNode = agentNodes[1];

        startNode.AgentKey.Should().Be($"{prefix}-developer");
        mechanicalGateNode.AgentKey.Should().Be($"{prefix}-mechanical-gate");
        reviewerNode.AgentKey.Should().Be($"{prefix}-reviewer");

        // Both gates declare Approved + Rejected; outer ReviewLoop loops on Rejected from
        // either source.
        mechanicalGateNode.OutputPorts.Should().Contain(new[] { "Approved", "Rejected" });
        reviewerNode.OutputPorts.Should().Contain(new[] { "Approved", "Rejected" });

        // Edge wiring: developer → mechanical-gate; mechanical-gate.Approved → reviewer.
        // mechanical-gate.Rejected has NO outgoing edge → terminal.
        var devToMech = inner.Edges.Single(e =>
            e.FromNodeId == startNode.Id && e.FromPort == "Continue");
        devToMech.ToNodeId.Should().Be(mechanicalGateNode.Id);

        var mechApprovedToReviewer = inner.Edges.Single(e =>
            e.FromNodeId == mechanicalGateNode.Id && e.FromPort == "Approved");
        mechApprovedToReviewer.ToNodeId.Should().Be(reviewerNode.Id);

        inner.Edges.Should().NotContain(e =>
            e.FromNodeId == mechanicalGateNode.Id && e.FromPort == "Rejected",
            "mechanical Rejected must be unwired so it terminates the subflow without invoking the reviewer");

        // Both Rejected paths surface as the same terminal port on the subflow, so the
        // outer ReviewLoop's loopDecision = "Rejected" loops on either.
        inner.TerminalPorts.Should().Contain("Rejected");
        inner.TerminalPorts.Should().Contain("Approved");
    }

    [Fact]
    public async Task MechanicalReviewLoopTemplate_OuterWorkflow_ConfiguresReviewLoopWithRejectionHistory()
    {
        // sc-273 acceptance: outer workflow's ReviewLoop carries loopDecision=Rejected,
        // maxRounds=5, and rejection-history enabled — so the developer's
        // {{ rejectionHistory }} accumulates feedback from BOTH mechanical and model gates
        // across rounds.
        AgentConfigRepository.ClearCacheForTests();
        await SeedSystemRolesAsync();
        var prefix = $"mech-outer-{Guid.NewGuid():N}";
        var materializer = CreateMaterializer();

        await materializer.MaterializeAsync(
            templateId: "mechanical-review-loop",
            namePrefix: prefix,
            createdBy: null);

        await using var verifyCtx = CreateDbContext();
        var workflowRepo = new WorkflowRepository(verifyCtx);

        var outer = await workflowRepo.GetAsync(prefix, 1);
        var loopNode = outer.Nodes.SingleOrDefault(n => n.Kind == WorkflowNodeKind.ReviewLoop);
        loopNode.Should().NotBeNull();
        loopNode!.SubflowKey.Should().Be($"{prefix}-inner");
        loopNode.SubflowVersion.Should().Be(1);
        loopNode.ReviewMaxRounds.Should().Be(5);
        loopNode.LoopDecision.Should().Be("Rejected");
        loopNode.RejectionHistory.Should().NotBeNull();
        loopNode.RejectionHistory!.Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task MechanicalReviewLoopTemplate_AgentsPinPartials()
    {
        // sc-273 acceptance: developer pins @codeflow/producer-base, reviewer pins
        // @codeflow/reviewer-base. The mechanical-gate intentionally does NOT pin a partial
        // because its job is deterministic command execution, not LLM-side review.
        AgentConfigRepository.ClearCacheForTests();
        await SeedSystemRolesAsync();
        var prefix = $"mech-pin-{Guid.NewGuid():N}";
        var materializer = CreateMaterializer();

        await materializer.MaterializeAsync(
            templateId: "mechanical-review-loop",
            namePrefix: prefix,
            createdBy: null);

        await using var verifyCtx = CreateDbContext();
        var agentRepo = new AgentConfigRepository(verifyCtx);

        var developer = await agentRepo.GetAsync($"{prefix}-developer", 1);
        developer.Configuration.PartialPins.Should().NotBeNull();
        developer.Configuration.PartialPins!.Should().Contain(p =>
            p.Key == "@codeflow/producer-base" && p.Version == 1);

        var mechanicalGate = await agentRepo.GetAsync($"{prefix}-mechanical-gate", 1);
        mechanicalGate.Configuration.PartialPins.Should().BeNullOrEmpty(
            "the mechanical gate is deterministic and doesn't need a reviewer-style partial");

        var reviewer = await agentRepo.GetAsync($"{prefix}-reviewer", 1);
        reviewer.Configuration.PartialPins.Should().NotBeNull();
        reviewer.Configuration.PartialPins!.Should().Contain(p =>
            p.Key == "@codeflow/reviewer-base" && p.Version == 1);
    }

    [Fact]
    public async Task MechanicalReviewLoopTemplate_AssignsCodeWorkerRoleToMechanicalGate_WhenSeeded()
    {
        // sc-273 acceptance: the mechanical-gate agent is auto-assigned the seeded
        // code-worker role at materialization time so it has run_command + read_file grants
        // out of the box. Operators can swap to a different role post-materialization.
        AgentConfigRepository.ClearCacheForTests();
        await SeedSystemRolesAsync();
        var prefix = $"mech-role-{Guid.NewGuid():N}";
        var materializer = CreateMaterializer();

        await materializer.MaterializeAsync(
            templateId: "mechanical-review-loop",
            namePrefix: prefix,
            createdBy: null);

        await using var verifyCtx = CreateDbContext();
        var roleRepo = new AgentRoleRepository(verifyCtx);

        var mechanicalGateRoles = await roleRepo.GetRolesForAgentAsync($"{prefix}-mechanical-gate");
        mechanicalGateRoles.Should().ContainSingle()
            .Which.Key.Should().Be(SystemAgentRoles.CodeWorkerKey);

        // Other agents in the template have no role assignments — they're LLM-only.
        var developerRoles = await roleRepo.GetRolesForAgentAsync($"{prefix}-developer");
        developerRoles.Should().BeEmpty();
        var reviewerRoles = await roleRepo.GetRolesForAgentAsync($"{prefix}-reviewer");
        reviewerRoles.Should().BeEmpty();
    }

    [Fact]
    public async Task MechanicalReviewLoopTemplate_Materializes_WhenCodeWorkerRoleNotSeeded()
    {
        // sc-273: a fresh DB without the code-worker seed should still materialize the
        // template — the role assignment is best-effort. Operator wires up an equivalent role
        // post-materialization if the system seeds aren't loaded.
        AgentConfigRepository.ClearCacheForTests();
        // intentionally NOT calling SeedSystemRolesAsync() here
        var prefix = $"mech-noseed-{Guid.NewGuid():N}";
        var materializer = CreateMaterializer();

        var result = await materializer.MaterializeAsync(
            templateId: "mechanical-review-loop",
            namePrefix: prefix,
            createdBy: null);

        result.CreatedEntities.Should().HaveCount(6);

        await using var verifyCtx = CreateDbContext();
        var roleRepo = new AgentRoleRepository(verifyCtx);
        var mechanicalGateRoles = await roleRepo.GetRolesForAgentAsync($"{prefix}-mechanical-gate");
        mechanicalGateRoles.Should().BeEmpty(
            "no system role to assign when the seeder hasn't run");
    }

    private async Task SeedSystemRolesAsync()
    {
        // The materializer test fixture brings up a fresh MariaDB schema via migrations but
        // doesn't run SystemAgentRoleSeeder. Tests that exercise the mechanical-gate role
        // assignment seed it explicitly so the assignment lands.
        await using var ctx = CreateDbContext();
        await SystemAgentRoleSeeder.SeedAsync(ctx);
    }

    [Fact]
    public void TemplateRegistry_LookupIsCaseInsensitive()
    {
        var registry = new WorkflowTemplateRegistry();

        registry.GetOrDefault("Empty-Workflow").Should().NotBeNull();
        registry.GetOrDefault("EMPTY-WORKFLOW").Should().NotBeNull();
        registry.GetOrDefault("empty-workflow").Should().NotBeNull();
        registry.GetOrDefault("nonexistent").Should().BeNull();
    }

    private CodeFlowDbContext CreateDbContext()
    {
        var builder = new DbContextOptionsBuilder<CodeFlowDbContext>();
        CodeFlowDbContextOptions.Configure(builder, connectionString!);
        return new CodeFlowDbContext(builder.Options);
    }

    private WorkflowTemplateMaterializer CreateMaterializer()
    {
        var ctx = CreateDbContext();
        return new WorkflowTemplateMaterializer(
            new WorkflowTemplateRegistry(),
            new AgentConfigRepository(ctx),
            new WorkflowRepository(ctx),
            new AgentRoleRepository(ctx),
            ctx);
    }
}
