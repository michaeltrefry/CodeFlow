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
        listed.Should().Contain(t => t.Id == "hitl-approval-gate"
            && t.Category == WorkflowTemplateCategory.Hitl);
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
