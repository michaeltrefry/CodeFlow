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
    public void TemplateRegistry_ListsEmptyWorkflowByDefault()
    {
        var registry = new WorkflowTemplateRegistry();

        var listed = registry.List();

        listed.Should().ContainSingle();
        listed[0].Id.Should().Be(WorkflowTemplateRegistry.EmptyWorkflowId);
        listed[0].Category.Should().Be(WorkflowTemplateCategory.Empty);
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
