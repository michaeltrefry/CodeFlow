using CodeFlow.Api.Dtos;
using CodeFlow.Api.Validation;
using CodeFlow.Persistence;
using CodeFlow.Runtime;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace CodeFlow.Api.Tests.Validation;

/// <summary>
/// sc-944 / FE-3: save-time validation rules for <see cref="WorkflowNodeKind.ForEach"/>. Each
/// test seeds a parent workflow Start → ForEach → Sink topology plus a child workflow the
/// ForEach references, swaps one ForEach-node field, and asserts the pass/fail outcome. The
/// validator's <c>ValidateForEachNode</c> is exercised end-to-end through
/// <c>WorkflowValidator.ValidateAsync</c> so cross-cutting rules (port allowance, subflow
/// resolution) are also covered.
/// </summary>
public sealed class ForEachValidatorTests
{
    private const string ChildKey = "foreach-child";

    [Fact]
    public async Task ForEachNode_HappyPath_Validates()
    {
        var fx = await CreateFixtureAsync();
        var (nodes, edges) = BuildForEachWorkflow(fx, node => node);

        var result = await fx.ValidateAsync("parent", nodes, edges);

        result.IsValid.Should().BeTrue(result.Error);
    }

    [Fact]
    public async Task ForEachNode_MissingCollectionExpression_Fails()
    {
        var fx = await CreateFixtureAsync();
        var (nodes, edges) = BuildForEachWorkflow(fx, node => node with { CollectionExpression = null });

        var result = await fx.ValidateAsync("parent", nodes, edges);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("collectionExpression");
    }

    [Fact]
    public async Task ForEachNode_UnparseableCollectionExpression_Fails()
    {
        var fx = await CreateFixtureAsync();
        // Unterminated bracket — Scriban's parser rejects.
        var (nodes, edges) = BuildForEachWorkflow(fx, node => node with
        {
            CollectionExpression = "workflow.items[",
        });

        var result = await fx.ValidateAsync("parent", nodes, edges);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("collectionExpression");
        result.Error.Should().Contain("parseable");
    }

    [Fact]
    public async Task ForEachNode_MissingItemVar_Fails()
    {
        var fx = await CreateFixtureAsync();
        var (nodes, edges) = BuildForEachWorkflow(fx, node => node with { ItemVar = null });

        var result = await fx.ValidateAsync("parent", nodes, edges);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("itemVar");
    }

    [Theory]
    [InlineData("1bad")]      // leading digit
    [InlineData("has-dash")]  // dash
    [InlineData("has space")] // whitespace
    public async Task ForEachNode_InvalidItemVarIdentifier_Fails(string badItemVar)
    {
        var fx = await CreateFixtureAsync();
        var (nodes, edges) = BuildForEachWorkflow(fx, node => node with { ItemVar = badItemVar });

        var result = await fx.ValidateAsync("parent", nodes, edges);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("valid identifier");
    }

    [Theory]
    [InlineData("index")]
    [InlineData("count")]
    [InlineData("isLast")]
    public async Task ForEachNode_ReservedItemVar_Fails(string reservedName)
    {
        var fx = await CreateFixtureAsync();
        var (nodes, edges) = BuildForEachWorkflow(fx, node => node with { ItemVar = reservedName });

        var result = await fx.ValidateAsync("parent", nodes, edges);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("reserved");
    }

    [Fact]
    public async Task ForEachNode_AllowsDefaultItemName()
    {
        // 'item' is the default and must stay allowed even though authors often customise it.
        var fx = await CreateFixtureAsync();
        var (nodes, edges) = BuildForEachWorkflow(fx, node => node with { ItemVar = "item" });

        var result = await fx.ValidateAsync("parent", nodes, edges);

        result.IsValid.Should().BeTrue(result.Error);
    }

    [Fact]
    public async Task ForEachNode_MissingSubflowKey_Fails()
    {
        var fx = await CreateFixtureAsync();
        var (nodes, edges) = BuildForEachWorkflow(fx, node => node with { SubflowKey = null });

        var result = await fx.ValidateAsync("parent", nodes, edges);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("SubflowKey");
    }

    [Fact]
    public async Task ForEachNode_SelfReference_Fails()
    {
        var fx = await CreateFixtureAsync();
        // SubflowKey points at the parent workflow's own key.
        var (nodes, edges) = BuildForEachWorkflow(fx, node => node with { SubflowKey = "parent" });

        var result = await fx.ValidateAsync("parent", nodes, edges);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("Self-referential");
    }

    [Fact]
    public async Task ForEachNode_NullSubflowVersion_AllowedAsLatestAtSave()
    {
        // Null SubflowVersion is the "latest at save" sentinel — same as Subflow / ReviewLoop.
        // The endpoint's ResolveSubflowLatestVersionsAsync pins the version before persistence.
        var fx = await CreateFixtureAsync();
        var (nodes, edges) = BuildForEachWorkflow(fx, node => node with { SubflowVersion = null });

        var result = await fx.ValidateAsync("parent", nodes, edges);

        result.IsValid.Should().BeTrue(result.Error);
    }

    [Fact]
    public async Task ForEachNode_UnknownSubflowKey_Fails()
    {
        // Existence is checked alongside Subflow / ReviewLoop in the workflow-level pass: a
        // ForEach referencing an unknown key must fail.
        var fx = await CreateFixtureAsync();
        var (nodes, edges) = BuildForEachWorkflow(fx, node => node with { SubflowKey = "does-not-exist" });

        var result = await fx.ValidateAsync("parent", nodes, edges);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("does-not-exist");
    }

    [Fact]
    public async Task ForEachNode_AuthorDeclaredOutputPort_Fails()
    {
        var fx = await CreateFixtureAsync();
        var (nodes, edges) = BuildForEachWorkflow(fx, node => node with
        {
            OutputPorts = new[] { "Custom" },
        });

        var result = await fx.ValidateAsync("parent", nodes, edges);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("Custom");
    }

    [Fact]
    public async Task ForEachNode_WithAgentKey_Fails()
    {
        var fx = await CreateFixtureAsync();
        var (nodes, edges) = BuildForEachWorkflow(fx, node => node with { AgentKey = "kickoff" });

        var result = await fx.ValidateAsync("parent", nodes, edges);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("AgentKey");
        result.Error.Should().Contain("control-flow");
    }

    private static async Task<TestFixture> CreateFixtureAsync()
    {
        var fx = await TestFixture.CreateAsync();
        await fx.SeedAgentAsync("kickoff", new[] { "Completed" });
        await fx.SeedAgentAsync("sink", new[] { "Completed" });
        await fx.SeedChildWorkflowAsync(ChildKey, version: 1);
        return fx;
    }

    private static (WorkflowNodeDto[] Nodes, WorkflowEdgeDto[] Edges) BuildForEachWorkflow(
        TestFixture _,
        Func<WorkflowNodeDto, WorkflowNodeDto> forEachCustomization)
    {
        var startId = Guid.NewGuid();
        var forEachId = Guid.NewGuid();
        var sinkId = Guid.NewGuid();

        var forEachNode = new WorkflowNodeDto(
            Id: forEachId,
            Kind: WorkflowNodeKind.ForEach,
            AgentKey: null,
            AgentVersion: null,
            OutputScript: null,
            OutputPorts: Array.Empty<string>(),
            LayoutX: 0,
            LayoutY: 0,
            SubflowKey: ChildKey,
            SubflowVersion: 1,
            CollectionExpression: "workflow.items",
            ItemVar: "task");

        forEachNode = forEachCustomization(forEachNode);

        var nodes = new[]
        {
            new WorkflowNodeDto(startId, WorkflowNodeKind.Start, "kickoff", 1, null,
                new[] { "Completed" }, 0, 0),
            forEachNode,
            new WorkflowNodeDto(sinkId, WorkflowNodeKind.Agent, "sink", 1, null,
                new[] { "Completed" }, 0, 0),
        };

        var edges = new[]
        {
            new WorkflowEdgeDto(startId, "Completed", forEachId, "in", false, 0),
            new WorkflowEdgeDto(forEachId, "Continue", sinkId, "in", false, 1),
        };

        return (nodes, edges);
    }

    private sealed class TestFixture
    {
        public CodeFlowDbContext DbContext { get; }
        public IWorkflowRepository WorkflowRepo { get; }
        public IAgentConfigRepository AgentRepo { get; }

        private TestFixture(CodeFlowDbContext db, IWorkflowRepository wr, IAgentConfigRepository ar)
        {
            DbContext = db;
            WorkflowRepo = wr;
            AgentRepo = ar;
        }

        public static Task<TestFixture> CreateAsync()
        {
            var options = new DbContextOptionsBuilder<CodeFlowDbContext>()
                .UseInMemoryDatabase($"foreach-validator-tests-{Guid.NewGuid():N}")
                .Options;
            var db = new CodeFlowDbContext(options);
            var workflowRepo = new WorkflowRepository(db);
            var agentRepo = new AgentConfigRepository(db);
            return Task.FromResult(new TestFixture(db, workflowRepo, agentRepo));
        }

        public async Task SeedAgentAsync(string key, IReadOnlyList<string> declaredOutputs, int version = 1)
        {
            var outputsJson = string.Join(",", declaredOutputs.Select(o =>
                $$"""{ "kind": "{{o}}" }"""));
            var configJson = $$"""
                {
                    "type": "agent",
                    "provider": "openai",
                    "model": "gpt-test",
                    "outputs": [ {{outputsJson}} ]
                }
                """;
            DbContext.Agents.Add(new AgentConfigEntity
            {
                Key = key,
                Version = version,
                ConfigJson = configJson,
                CreatedAtUtc = DateTime.UtcNow,
                IsActive = true,
            });
            await DbContext.SaveChangesAsync();
        }

        public async Task SeedChildWorkflowAsync(string key, int version)
        {
            DbContext.Workflows.Add(new WorkflowEntity
            {
                Key = key,
                Version = version,
                Name = "ForEach child",
                MaxStepsPerSaga = 10,
                CreatedAtUtc = DateTime.UtcNow,
                Nodes =
                [
                    new WorkflowNodeEntity
                    {
                        NodeId = Guid.NewGuid(),
                        Kind = WorkflowNodeKind.Start,
                        AgentKey = "kickoff",
                        AgentVersion = 1,
                        OutputPortsJson = """["Completed"]""",
                    },
                ],
                Edges = []
            });
            await DbContext.SaveChangesAsync();
        }

        public Task<ValidationResult> ValidateAsync(
            string key,
            IReadOnlyList<WorkflowNodeDto> nodes,
            IReadOnlyList<WorkflowEdgeDto> edges,
            IReadOnlyList<WorkflowInputDto>? inputs = null) =>
            WorkflowValidator.ValidateAsync(
                key,
                $"Test workflow {key}",
                maxRoundsPerRound: 3,
                nodes,
                edges,
                inputs,
                DbContext,
                WorkflowRepo,
                AgentRepo,
                CancellationToken.None);
    }
}
