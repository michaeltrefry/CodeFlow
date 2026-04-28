using CodeFlow.Api.Dtos;
using CodeFlow.Api.Validation;
using CodeFlow.Persistence;
using CodeFlow.Runtime;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace CodeFlow.Api.Tests.Validation;

/// <summary>
/// sc-43: save-time validation rules for <see cref="WorkflowNodeKind.Swarm"/>. Each test seeds
/// a minimal Start → Swarm topology, swaps one Swarm-node field, and asserts the pass/fail
/// outcome. The validator's <c>ValidateSwarmNode</c> is exercised end-to-end through
/// <c>WorkflowValidator.ValidateAsync</c> so any cross-cutting rules (port reservation,
/// agent existence, edge port allowance) are also covered.
/// </summary>
public sealed class SwarmValidatorTests
{
    [Fact]
    public async Task SwarmNode_Sequential_HappyPath_Validates()
    {
        var fx = await CreateFixtureAsync();
        var (nodes, edges) = BuildSwarmWorkflow(fx, ConfigureSequential);

        var result = await fx.ValidateAsync("parent", nodes, edges);

        result.IsValid.Should().BeTrue(result.Error);
    }

    [Fact]
    public async Task SwarmNode_Coordinator_HappyPath_Validates()
    {
        var fx = await CreateFixtureAsync();
        var (nodes, edges) = BuildSwarmWorkflow(fx, ConfigureCoordinator);

        var result = await fx.ValidateAsync("parent", nodes, edges);

        result.IsValid.Should().BeTrue(result.Error);
    }

    [Fact]
    public async Task SwarmNode_MissingProtocol_Fails()
    {
        var fx = await CreateFixtureAsync();
        var (nodes, edges) = BuildSwarmWorkflow(fx, swarm => swarm with { SwarmProtocol = null });

        var result = await fx.ValidateAsync("parent", nodes, edges);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("protocol");
    }

    [Fact]
    public async Task SwarmNode_UnknownProtocol_Fails()
    {
        var fx = await CreateFixtureAsync();
        var (nodes, edges) = BuildSwarmWorkflow(fx, swarm => swarm with { SwarmProtocol = "Broadcast" });

        var result = await fx.ValidateAsync("parent", nodes, edges);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("Broadcast");
    }

    [Fact]
    public async Task SwarmNode_NMissing_Fails()
    {
        var fx = await CreateFixtureAsync();
        var (nodes, edges) = BuildSwarmWorkflow(fx, swarm => swarm with { SwarmN = null });

        var result = await fx.ValidateAsync("parent", nodes, edges);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("n");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    public async Task SwarmNode_NOutOfRange_Fails(int badN)
    {
        var fx = await CreateFixtureAsync();
        var (nodes, edges) = BuildSwarmWorkflow(fx, swarm => swarm with { SwarmN = badN });

        var result = await fx.ValidateAsync("parent", nodes, edges);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain($"n = {badN}");
    }

    [Fact]
    public async Task SwarmNode_Sequential_WithCoordinatorAgent_Fails()
    {
        var fx = await CreateFixtureAsync();
        var (nodes, edges) = BuildSwarmWorkflow(fx, swarm => swarm with
        {
            CoordinatorAgentKey = "swarm-coordinator",
            CoordinatorAgentVersion = 1,
        });

        var result = await fx.ValidateAsync("parent", nodes, edges);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("Coordinator");
    }

    [Fact]
    public async Task SwarmNode_Coordinator_WithoutCoordinatorAgent_Fails()
    {
        var fx = await CreateFixtureAsync();
        var (nodes, edges) = BuildSwarmWorkflow(fx, swarm => ConfigureCoordinator(swarm) with
        {
            CoordinatorAgentKey = null,
            CoordinatorAgentVersion = null,
        });

        var result = await fx.ValidateAsync("parent", nodes, edges);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("CoordinatorAgentKey");
    }

    [Fact]
    public async Task SwarmNode_TokenBudgetZero_Fails()
    {
        var fx = await CreateFixtureAsync();
        var (nodes, edges) = BuildSwarmWorkflow(fx, swarm => swarm with { SwarmTokenBudget = 0 });

        var result = await fx.ValidateAsync("parent", nodes, edges);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("SwarmTokenBudget");
    }

    [Fact]
    public async Task SwarmNode_NoOutputPorts_Fails()
    {
        var fx = await CreateFixtureAsync();
        var (nodes, edges) = BuildSwarmWorkflow(fx, swarm => swarm with { OutputPorts = Array.Empty<string>() });

        // Drop the swarm→sink edge since the swarm has no port to leave from.
        var prunedEdges = edges.Where(e => e.FromNodeId == nodes[0].Id).ToArray();

        var result = await fx.ValidateAsync("parent", nodes, prunedEdges);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("output port");
    }

    [Fact]
    public async Task SwarmNode_UnknownContributorAgent_Fails()
    {
        var fx = await CreateFixtureAsync();
        var (nodes, edges) = BuildSwarmWorkflow(fx, swarm => swarm with
        {
            ContributorAgentKey = "agent-that-does-not-exist",
        });

        var result = await fx.ValidateAsync("parent", nodes, edges);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("agent-that-does-not-exist");
    }

    private static async Task<TestFixture> CreateFixtureAsync()
    {
        var fx = await TestFixture.CreateAsync();
        await fx.SeedAgentAsync("kickoff", new[] { "Completed" });
        await fx.SeedAgentAsync("sink", new[] { "Completed" });
        await fx.SeedAgentAsync("swarm-contributor", new[] { "Contributed" });
        await fx.SeedAgentAsync("swarm-synthesizer", new[] { "Synthesized" });
        await fx.SeedAgentAsync("swarm-coordinator", new[] { "Assigned" });
        return fx;
    }

    private static (WorkflowNodeDto[] Nodes, WorkflowEdgeDto[] Edges) BuildSwarmWorkflow(
        TestFixture _,
        Func<WorkflowNodeDto, WorkflowNodeDto> swarmCustomization)
    {
        var startId = Guid.NewGuid();
        var swarmId = Guid.NewGuid();
        var sinkId = Guid.NewGuid();

        var swarmNode = new WorkflowNodeDto(
            Id: swarmId,
            Kind: WorkflowNodeKind.Swarm,
            AgentKey: null,
            AgentVersion: null,
            OutputScript: null,
            OutputPorts: new[] { "Synthesized" },
            LayoutX: 0,
            LayoutY: 0,
            SwarmProtocol: "Sequential",
            SwarmN: 2,
            ContributorAgentKey: "swarm-contributor",
            ContributorAgentVersion: 1,
            SynthesizerAgentKey: "swarm-synthesizer",
            SynthesizerAgentVersion: 1,
            CoordinatorAgentKey: null,
            CoordinatorAgentVersion: null,
            SwarmTokenBudget: null);

        swarmNode = swarmCustomization(swarmNode);

        var nodes = new[]
        {
            new WorkflowNodeDto(startId, WorkflowNodeKind.Start, "kickoff", 1, null,
                new[] { "Completed" }, 0, 0),
            swarmNode,
            new WorkflowNodeDto(sinkId, WorkflowNodeKind.Agent, "sink", 1, null,
                new[] { "Completed" }, 0, 0),
        };

        var edges = new[]
        {
            new WorkflowEdgeDto(startId, "Completed", swarmId, "in", false, 0),
            new WorkflowEdgeDto(swarmId, "Synthesized", sinkId, "in", false, 1),
        };

        return (nodes, edges);
    }

    private static WorkflowNodeDto ConfigureSequential(WorkflowNodeDto node) =>
        node; // BuildSwarmWorkflow defaults to Sequential.

    private static WorkflowNodeDto ConfigureCoordinator(WorkflowNodeDto node) =>
        node with
        {
            SwarmProtocol = "Coordinator",
            CoordinatorAgentKey = "swarm-coordinator",
            CoordinatorAgentVersion = 1,
        };

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
                .UseInMemoryDatabase($"swarm-validator-tests-{Guid.NewGuid():N}")
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
