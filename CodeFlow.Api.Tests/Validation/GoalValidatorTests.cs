using CodeFlow.Api.Dtos;
using CodeFlow.Api.Validation;
using CodeFlow.Persistence;
using CodeFlow.Runtime;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace CodeFlow.Api.Tests.Validation;

/// <summary>
/// Epic 978 / GN-1: save-time validation rules for <see cref="WorkflowNodeKind.Goal"/>. Each
/// test seeds a Start → Goal → Sink topology, swaps one Goal-node field, and asserts the
/// pass/fail outcome. The validator's <c>ValidateGoalNode</c> is exercised end-to-end
/// through <c>WorkflowValidator.ValidateAsync</c> so cross-cutting rules (port allowance,
/// agent reference resolution) are also covered.
/// </summary>
public sealed class GoalValidatorTests
{
    [Fact]
    public async Task GoalNode_HappyPath_Validates()
    {
        var fx = await CreateFixtureAsync();
        var (nodes, edges) = BuildGoalWorkflow(fx, node => node);

        var result = await fx.ValidateAsync("parent", nodes, edges);

        result.IsValid.Should().BeTrue(result.Error);
    }

    [Fact]
    public async Task GoalNode_MissingAgentKey_Fails()
    {
        var fx = await CreateFixtureAsync();
        var (nodes, edges) = BuildGoalWorkflow(fx, node => node with { AgentKey = null });

        var result = await fx.ValidateAsync("parent", nodes, edges);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("AgentKey");
    }

    [Fact]
    public async Task GoalNode_MissingObjective_Fails()
    {
        var fx = await CreateFixtureAsync();
        var (nodes, edges) = BuildGoalWorkflow(fx, node => node with { GoalObjective = null });

        var result = await fx.ValidateAsync("parent", nodes, edges);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("goalObjective");
    }

    [Fact]
    public async Task GoalNode_EmptyObjective_Fails()
    {
        var fx = await CreateFixtureAsync();
        var (nodes, edges) = BuildGoalWorkflow(fx, node => node with { GoalObjective = "   " });

        var result = await fx.ValidateAsync("parent", nodes, edges);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("goalObjective");
    }

    [Fact]
    public async Task GoalNode_UnparseableObjective_Fails()
    {
        var fx = await CreateFixtureAsync();
        // Unterminated `{{ if` — Scriban's parser rejects.
        var (nodes, edges) = BuildGoalWorkflow(fx, node => node with
        {
            GoalObjective = "Complete the story {{ if workflow.story",
        });

        var result = await fx.ValidateAsync("parent", nodes, edges);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("goalObjective");
        result.Error.Should().Contain("parseable");
    }

    [Fact]
    public async Task GoalNode_ObjectiveAllowsScribanPlaceholders()
    {
        var fx = await CreateFixtureAsync();
        // Placeholders that reference workflow vars must parse cleanly even though the vars
        // aren't bound at save time (they're resolved per-iteration in GN-3).
        var (nodes, edges) = BuildGoalWorkflow(fx, node => node with
        {
            GoalObjective = "Complete story {{ workflow.story_id }}: {{ workflow.story_title }}",
        });

        var result = await fx.ValidateAsync("parent", nodes, edges);

        result.IsValid.Should().BeTrue(result.Error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100000)]
    public async Task GoalNode_NonPositiveTokenBudget_Fails(int badBudget)
    {
        var fx = await CreateFixtureAsync();
        var (nodes, edges) = BuildGoalWorkflow(fx, node => node with { GoalTokenBudget = badBudget });

        var result = await fx.ValidateAsync("parent", nodes, edges);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("goalTokenBudget");
    }

    [Fact]
    public async Task GoalNode_NullTokenBudget_AllowedAsUnbounded()
    {
        // Null tokenBudget = unbounded run. The iteration cap is the safety net.
        var fx = await CreateFixtureAsync();
        var (nodes, edges) = BuildGoalWorkflow(fx, node => node with { GoalTokenBudget = null });

        var result = await fx.ValidateAsync("parent", nodes, edges);

        result.IsValid.Should().BeTrue(result.Error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(501)]
    [InlineData(int.MaxValue)]
    public async Task GoalNode_OutOfRangeMaxIterations_Fails(int badIterations)
    {
        var fx = await CreateFixtureAsync();
        var (nodes, edges) = BuildGoalWorkflow(fx, node => node with { GoalMaxIterations = badIterations });

        var result = await fx.ValidateAsync("parent", nodes, edges);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("goalMaxIterations");
    }

    [Fact]
    public async Task GoalNode_NullMaxIterations_AllowedAsDefault()
    {
        // Null applies the runtime default (50).
        var fx = await CreateFixtureAsync();
        var (nodes, edges) = BuildGoalWorkflow(fx, node => node with { GoalMaxIterations = null });

        var result = await fx.ValidateAsync("parent", nodes, edges);

        result.IsValid.Should().BeTrue(result.Error);
    }

    [Fact]
    public async Task GoalNode_AuthorDeclaredOutputPort_Fails()
    {
        var fx = await CreateFixtureAsync();
        var (nodes, edges) = BuildGoalWorkflow(fx, node => node with
        {
            OutputPorts = new[] { "Custom" },
        });

        var result = await fx.ValidateAsync("parent", nodes, edges);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("Custom");
        // Error message must enumerate all three synthesized ports so the author knows what
        // the runtime will actually emit. Pin all three by name.
        result.Error.Should().Contain("Success");
        result.Error.Should().Contain("BudgetLimited");
        result.Error.Should().Contain("Abandoned");
    }

    [Fact]
    public async Task GoalNode_EdgeFromAbandonedPort_Validates()
    {
        // The Abandoned port is synthesized by the validator (GN-7). An edge that routes
        // Abandoned to a downstream postmortem/HITL handler must be accepted without the
        // author declaring `outputPorts: ["Abandoned"]` (declaring it would fail per the
        // test above).
        var fx = await CreateFixtureAsync();

        var startId = Guid.NewGuid();
        var goalId = Guid.NewGuid();
        var sinkId = Guid.NewGuid();
        var postmortemId = Guid.NewGuid();

        var nodes = new[]
        {
            new WorkflowNodeDto(startId, WorkflowNodeKind.Start, "kickoff", 1, null,
                new[] { "Completed" }, 0, 0),
            new WorkflowNodeDto(goalId, WorkflowNodeKind.Goal, "goal-runner", 1, null,
                Array.Empty<string>(), 0, 0,
                GoalObjective: "Complete the story acceptance criteria",
                GoalTokenBudget: 500_000,
                GoalMaxIterations: 50),
            new WorkflowNodeDto(sinkId, WorkflowNodeKind.Agent, "sink", 1, null,
                new[] { "Completed" }, 0, 0),
            new WorkflowNodeDto(postmortemId, WorkflowNodeKind.Agent, "sink", 1, null,
                new[] { "Completed" }, 0, 0),
        };

        var edges = new[]
        {
            new WorkflowEdgeDto(startId, "Completed", goalId, "in", false, 0),
            new WorkflowEdgeDto(goalId, "Success", sinkId, "in", false, 1),
            new WorkflowEdgeDto(goalId, "Abandoned", postmortemId, "in", false, 2),
        };

        var result = await fx.ValidateAsync("parent", nodes, edges);

        result.IsValid.Should().BeTrue(result.Error ?? string.Empty);
    }

    private static async Task<TestFixture> CreateFixtureAsync()
    {
        var fx = await TestFixture.CreateAsync();
        await fx.SeedAgentAsync("kickoff", new[] { "Completed" });
        await fx.SeedAgentAsync("goal-runner", new[] { "Completed" });
        await fx.SeedAgentAsync("sink", new[] { "Completed" });
        return fx;
    }

    private static (WorkflowNodeDto[] Nodes, WorkflowEdgeDto[] Edges) BuildGoalWorkflow(
        TestFixture _,
        Func<WorkflowNodeDto, WorkflowNodeDto> goalCustomization)
    {
        var startId = Guid.NewGuid();
        var goalId = Guid.NewGuid();
        var sinkId = Guid.NewGuid();

        var goalNode = new WorkflowNodeDto(
            Id: goalId,
            Kind: WorkflowNodeKind.Goal,
            AgentKey: "goal-runner",
            AgentVersion: 1,
            OutputScript: null,
            OutputPorts: Array.Empty<string>(),
            LayoutX: 0,
            LayoutY: 0,
            GoalObjective: "Complete the story acceptance criteria",
            GoalTokenBudget: 500_000,
            GoalMaxIterations: 50);

        goalNode = goalCustomization(goalNode);

        var nodes = new[]
        {
            new WorkflowNodeDto(startId, WorkflowNodeKind.Start, "kickoff", 1, null,
                new[] { "Completed" }, 0, 0),
            goalNode,
            new WorkflowNodeDto(sinkId, WorkflowNodeKind.Agent, "sink", 1, null,
                new[] { "Completed" }, 0, 0),
        };

        var edges = new[]
        {
            new WorkflowEdgeDto(startId, "Completed", goalId, "in", false, 0),
            new WorkflowEdgeDto(goalId, "Success", sinkId, "in", false, 1),
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
                .UseInMemoryDatabase($"goal-validator-tests-{Guid.NewGuid():N}")
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
