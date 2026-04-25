using CodeFlow.Api.Dtos;
using CodeFlow.Api.Validation;
using CodeFlow.Persistence;
using CodeFlow.Runtime;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace CodeFlow.Api.Tests.Validation;

public sealed class WorkflowValidatorTests
{
    [Fact]
    public async Task SubflowEdge_ShouldAcceptChildTerminalPort()
    {
        var fx = await TestFixture.CreateAsync();
        await fx.SeedAgentAsync("kickoff", new[] { "Completed" });
        await fx.SeedChildWorkflowAsync(
            "child-flow",
            version: 1,
            (WorkflowNodeKind.Start, "kickoff", new[] { "Completed" }, IsTerminal: true),
            (WorkflowNodeKind.Agent, "leftLeaf", new[] { "Approved" }, IsTerminal: true),
            (WorkflowNodeKind.Agent, "rightLeaf", new[] { "Rejected" }, IsTerminal: true));

        var startId = Guid.NewGuid();
        var subflowId = Guid.NewGuid();
        var leafApprovedId = Guid.NewGuid();
        var leafRejectedId = Guid.NewGuid();

        var nodes = new[]
        {
            Node(startId, WorkflowNodeKind.Start, "kickoff", new[] { "Completed" }),
            new WorkflowNodeDto(subflowId, WorkflowNodeKind.Subflow, AgentKey: null, AgentVersion: null,
                OutputScript: null, OutputPorts: new[] { "Approved", "Rejected" }, LayoutX: 0, LayoutY: 0,
                SubflowKey: "child-flow", SubflowVersion: 1),
            Node(leafApprovedId, WorkflowNodeKind.Agent, "kickoff", new[] { "Completed" }),
            Node(leafRejectedId, WorkflowNodeKind.Agent, "kickoff", new[] { "Completed" }),
        };

        var edges = new[]
        {
            new WorkflowEdgeDto(startId, "Completed", subflowId, "in", false, 0),
            new WorkflowEdgeDto(subflowId, "Approved", leafApprovedId, "in", false, 1),
            new WorkflowEdgeDto(subflowId, "Rejected", leafRejectedId, "in", false, 2),
        };

        var result = await fx.ValidateAsync("parent", nodes, edges);

        result.IsValid.Should().BeTrue("subflow edges may use any terminal port from the child workflow.");
    }

    [Fact]
    public async Task SubflowEdge_ShouldRejectPortNotInChildTerminalSet()
    {
        var fx = await TestFixture.CreateAsync();
        await fx.SeedAgentAsync("kickoff", new[] { "Completed" });
        await fx.SeedChildWorkflowAsync(
            "child-flow",
            version: 1,
            (WorkflowNodeKind.Start, "kickoff", new[] { "Completed" }, IsTerminal: true));

        var startId = Guid.NewGuid();
        var subflowId = Guid.NewGuid();
        var leafId = Guid.NewGuid();

        var nodes = new[]
        {
            Node(startId, WorkflowNodeKind.Start, "kickoff", new[] { "Completed" }),
            new WorkflowNodeDto(subflowId, WorkflowNodeKind.Subflow, AgentKey: null, AgentVersion: null,
                OutputScript: null, OutputPorts: new[] { "Completed" }, LayoutX: 0, LayoutY: 0,
                SubflowKey: "child-flow", SubflowVersion: 1),
            Node(leafId, WorkflowNodeKind.Agent, "kickoff", new[] { "Completed" }),
        };

        var edges = new[]
        {
            new WorkflowEdgeDto(startId, "Completed", subflowId, "in", false, 0),
            new WorkflowEdgeDto(subflowId, "Whatever", leafId, "in", false, 1),
        };

        var result = await fx.ValidateAsync("parent", nodes, edges);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("'Whatever'").And.Contain("Completed");
    }

    [Fact]
    public async Task ReviewLoopEdge_ShouldAcceptExhaustedAndLoopDecisionAndChildTerminal()
    {
        var fx = await TestFixture.CreateAsync();
        await fx.SeedAgentAsync("kickoff", new[] { "Completed" });
        await fx.SeedChildWorkflowAsync(
            "draft-critique",
            version: 1,
            (WorkflowNodeKind.Start, "kickoff", new[] { "Approved", "Rejected" }, IsTerminal: true));

        var startId = Guid.NewGuid();
        var loopId = Guid.NewGuid();
        var approvedSink = Guid.NewGuid();
        var exhaustedSink = Guid.NewGuid();
        var rejectedSink = Guid.NewGuid();

        var nodes = new[]
        {
            Node(startId, WorkflowNodeKind.Start, "kickoff", new[] { "Completed" }),
            new WorkflowNodeDto(loopId, WorkflowNodeKind.ReviewLoop, AgentKey: null, AgentVersion: null,
                OutputScript: null, OutputPorts: new[] { "Approved" }, LayoutX: 0, LayoutY: 0,
                SubflowKey: "draft-critique", SubflowVersion: 1, ReviewMaxRounds: 3, LoopDecision: "Rejected"),
            Node(approvedSink, WorkflowNodeKind.Agent, "kickoff", new[] { "Completed" }),
            Node(exhaustedSink, WorkflowNodeKind.Agent, "kickoff", new[] { "Completed" }),
            Node(rejectedSink, WorkflowNodeKind.Agent, "kickoff", new[] { "Completed" }),
        };

        var edges = new[]
        {
            new WorkflowEdgeDto(startId, "Completed", loopId, "in", false, 0),
            new WorkflowEdgeDto(loopId, "Approved", approvedSink, "in", false, 1),
            new WorkflowEdgeDto(loopId, "Exhausted", exhaustedSink, "in", false, 2),
            new WorkflowEdgeDto(loopId, "Rejected", rejectedSink, "in", false, 3),
        };

        var result = await fx.ValidateAsync("parent", nodes, edges);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task AgentNode_ShouldRejectPortsThatAgentDoesNotDeclare()
    {
        var fx = await TestFixture.CreateAsync();
        await fx.SeedAgentAsync("router", new[] { "Left", "Right" });
        await fx.SeedAgentAsync("kickoff", new[] { "Completed" });

        var startId = Guid.NewGuid();
        var routerId = Guid.NewGuid();
        var leftId = Guid.NewGuid();
        var middleId = Guid.NewGuid();

        var nodes = new[]
        {
            Node(startId, WorkflowNodeKind.Start, "kickoff", new[] { "Completed" }),
            Node(routerId, WorkflowNodeKind.Agent, "router", new[] { "Left", "Middle" }),
            Node(leftId, WorkflowNodeKind.Agent, "kickoff", new[] { "Completed" }),
            Node(middleId, WorkflowNodeKind.Agent, "kickoff", new[] { "Completed" }),
        };

        var edges = new[]
        {
            new WorkflowEdgeDto(startId, "Completed", routerId, "in", false, 0),
            new WorkflowEdgeDto(routerId, "Left", leftId, "in", false, 1),
            new WorkflowEdgeDto(routerId, "Middle", middleId, "in", false, 2),
        };

        var result = await fx.ValidateAsync("parent", nodes, edges);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("Middle").And.Contain("router");
    }

    [Fact]
    public async Task DeclaringFailedExplicitly_ShouldFailWithImplicitMessage()
    {
        var fx = await TestFixture.CreateAsync();
        await fx.SeedAgentAsync("kickoff", new[] { "Completed", "Failed" });

        var startId = Guid.NewGuid();
        var nodes = new[]
        {
            new WorkflowNodeDto(startId, WorkflowNodeKind.Start, "kickoff", 1, null,
                new[] { "Completed", "Failed" }, 0, 0),
        };

        var result = await fx.ValidateAsync("parent", nodes, Array.Empty<WorkflowEdgeDto>());

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("Failed").And.Contain("implicit");
    }

    [Fact]
    public async Task DeclaringExhaustedOnAnyNode_ShouldFailAsReserved()
    {
        var fx = await TestFixture.CreateAsync();
        await fx.SeedAgentAsync("kickoff", new[] { "Completed", "Exhausted" });

        var startId = Guid.NewGuid();
        var nodes = new[]
        {
            new WorkflowNodeDto(startId, WorkflowNodeKind.Start, "kickoff", 1, null,
                new[] { "Completed", "Exhausted" }, 0, 0),
        };

        var result = await fx.ValidateAsync("parent", nodes, Array.Empty<WorkflowEdgeDto>());

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("Exhausted").And.Contain("reserved");
    }

    [Fact]
    public async Task FailedEdge_ShouldBeAcceptedFromAnyNodeWithoutDeclaration()
    {
        var fx = await TestFixture.CreateAsync();
        await fx.SeedAgentAsync("kickoff", new[] { "Completed" });

        var startId = Guid.NewGuid();
        var failedSinkId = Guid.NewGuid();

        var nodes = new[]
        {
            Node(startId, WorkflowNodeKind.Start, "kickoff", new[] { "Completed" }),
            Node(failedSinkId, WorkflowNodeKind.Agent, "kickoff", new[] { "Completed" }),
        };

        var edges = new[]
        {
            new WorkflowEdgeDto(startId, "Failed", failedSinkId, "in", false, 0),
        };

        var result = await fx.ValidateAsync("parent", nodes, edges);

        result.IsValid.Should().BeTrue("Failed is implicit on every node and is always a valid edge source.");
    }

    private static WorkflowNodeDto Node(
        Guid id,
        WorkflowNodeKind kind,
        string agentKey,
        IReadOnlyList<string> outputPorts) =>
        new(id, kind, agentKey, 1, null, outputPorts, 0, 0);

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
                .UseInMemoryDatabase($"validator-tests-{Guid.NewGuid():N}")
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

        public async Task SeedChildWorkflowAsync(
            string key,
            int version,
            params (WorkflowNodeKind Kind, string AgentKey, string[] Ports, bool IsTerminal)[] nodeSpecs)
        {
            var nodes = nodeSpecs.Select(spec => new WorkflowNodeEntity
            {
                NodeId = Guid.NewGuid(),
                Kind = spec.Kind,
                AgentKey = spec.AgentKey,
                AgentVersion = 1,
                OutputPortsJson = JsonSerializer.Serialize(spec.Ports),
                LayoutX = 0,
                LayoutY = 0,
            }).ToList();

            // Edges: only generate edges for non-terminal entries; terminal entries leave their
            // ports unwired so TerminalPorts picks them up. For our test cases all nodes are
            // marked terminal except the explicit Start.
            DbContext.Workflows.Add(new WorkflowEntity
            {
                Key = key,
                Version = version,
                Name = key,
                MaxRoundsPerRound = 1,
                CreatedAtUtc = DateTime.UtcNow,
                Nodes = nodes,
                Edges = [],
                Inputs = [],
            });
            await DbContext.SaveChangesAsync();
        }

        public Task<ValidationResult> ValidateAsync(
            string key,
            IReadOnlyList<WorkflowNodeDto> nodes,
            IReadOnlyList<WorkflowEdgeDto> edges) =>
            WorkflowValidator.ValidateAsync(
                key,
                $"Test workflow {key}",
                maxRoundsPerRound: 3,
                nodes,
                edges,
                inputs: null,
                DbContext,
                WorkflowRepo,
                AgentRepo,
                CancellationToken.None);
    }
}
