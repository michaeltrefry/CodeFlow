using CodeFlow.Api.Dtos;
using CodeFlow.Api.Validation.Pipeline;
using CodeFlow.Api.Validation.Pipeline.Rules;
using CodeFlow.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace CodeFlow.Api.Tests.Validation;

/// <summary>
/// Tests for V4's <see cref="PortCouplingRule"/>: diff between agent declared outputs and node
/// wiring, including implicit-Failed handling and the error/warning split.
/// </summary>
public sealed class PortCouplingRuleTests
{
    [Fact]
    public async Task NodeWiresPortAgentDoesNotDeclare_EmitsErrorPerExtraPort()
    {
        await using var fx = await TestFixture.CreateAsync();
        await fx.SeedAgentAsync("reviewer", new[] { "Approved", "Rejected" });
        var nodeId = Guid.NewGuid();
        var nodes = new[]
        {
            AgentNode(nodeId, "reviewer", version: 1, ports: new[] { "Approved", "Cancelled" }),
        };

        var findings = await fx.RunRuleAsync(nodes);

        var errors = findings.Where(f => f.Severity == WorkflowValidationSeverity.Error).ToArray();
        errors.Should().HaveCount(1);
        errors[0].RuleId.Should().Be("port-coupling");
        errors[0].Message.Should().Contain("'Cancelled'");
        errors[0].Message.Should().Contain("reviewer");
        errors[0].Location!.NodeId.Should().Be(nodeId);
    }

    [Fact]
    public async Task AgentDeclaresPortNodeDoesNotWire_EmitsWarningPerMissingPort()
    {
        await using var fx = await TestFixture.CreateAsync();
        await fx.SeedAgentAsync("reviewer", new[] { "Approved", "Rejected" });
        var nodeId = Guid.NewGuid();
        var nodes = new[]
        {
            AgentNode(nodeId, "reviewer", version: 1, ports: new[] { "Approved" }),
        };

        var findings = await fx.RunRuleAsync(nodes);

        var warnings = findings.Where(f => f.Severity == WorkflowValidationSeverity.Warning).ToArray();
        warnings.Should().HaveCount(1);
        warnings[0].Message.Should().Contain("'Rejected'");
        warnings[0].Location!.NodeId.Should().Be(nodeId);
    }

    [Fact]
    public async Task DeclaredAndWiredMismatch_EmitsBothErrorAndWarning()
    {
        // Acceptance example from the spec: agent declares [Approved, Rejected],
        // node wires [Approved, Cancelled] — Cancelled errors, Rejected warns.
        await using var fx = await TestFixture.CreateAsync();
        await fx.SeedAgentAsync("reviewer", new[] { "Approved", "Rejected" });
        var nodeId = Guid.NewGuid();
        var nodes = new[]
        {
            AgentNode(nodeId, "reviewer", version: 1, ports: new[] { "Approved", "Cancelled" }),
        };

        var findings = await fx.RunRuleAsync(nodes);

        findings.Should().HaveCount(2);
        findings.Should().Contain(f =>
            f.Severity == WorkflowValidationSeverity.Error && f.Message.Contains("'Cancelled'"));
        findings.Should().Contain(f =>
            f.Severity == WorkflowValidationSeverity.Warning && f.Message.Contains("'Rejected'"));
    }

    [Fact]
    public async Task ImplicitFailedPortOnNode_DoesNotErrorEvenWhenAgentDoesNotDeclareIt()
    {
        // The implicit Failed port is always submittable; never error on it.
        await using var fx = await TestFixture.CreateAsync();
        await fx.SeedAgentAsync("worker", new[] { "Done" });
        var nodes = new[]
        {
            AgentNode(Guid.NewGuid(), "worker", version: 1, ports: new[] { "Done", "Failed" }),
        };

        var findings = await fx.RunRuleAsync(nodes);

        findings.Should().BeEmpty();
    }

    [Fact]
    public async Task PortsMatch_NoFindings()
    {
        await using var fx = await TestFixture.CreateAsync();
        await fx.SeedAgentAsync("reviewer", new[] { "Approved", "Rejected" });
        var nodes = new[]
        {
            AgentNode(Guid.NewGuid(), "reviewer", version: 1, ports: new[] { "Approved", "Rejected" }),
        };

        var findings = await fx.RunRuleAsync(nodes);

        findings.Should().BeEmpty();
    }

    [Fact]
    public async Task AgentVersionUnpinned_ResolvesLatestVersion()
    {
        await using var fx = await TestFixture.CreateAsync();
        await fx.SeedAgentAsync("reviewer", new[] { "Approved" }, version: 1);
        await fx.SeedAgentAsync("reviewer", new[] { "Approved", "Rejected" }, version: 2);
        var nodes = new[]
        {
            // null version → use latest (v2 declares Approved + Rejected) → expect Rejected unwired warning.
            AgentNode(Guid.NewGuid(), "reviewer", version: null, ports: new[] { "Approved" }),
        };

        var findings = await fx.RunRuleAsync(nodes);

        findings.Should().ContainSingle();
        findings[0].Severity.Should().Be(WorkflowValidationSeverity.Warning);
        findings[0].Message.Should().Contain("v2");
    }

    [Fact]
    public async Task UnknownAgent_SilentlySkipsToAvoidPilingOnLegacyValidatorMessage()
    {
        await using var fx = await TestFixture.CreateAsync();
        // No SeedAgentAsync — the agent reference is dangling. Legacy validator already surfaces
        // "Workflow references unknown agent(s)"; this rule must not double-up.
        var nodes = new[]
        {
            AgentNode(Guid.NewGuid(), "ghost", version: 1, ports: new[] { "Approved" }),
        };

        var findings = await fx.RunRuleAsync(nodes);

        findings.Should().BeEmpty();
    }

    [Fact]
    public async Task NodeWithoutAgentKey_Skipped()
    {
        await using var fx = await TestFixture.CreateAsync();
        var nodes = new[]
        {
            new WorkflowNodeDto(
                Id: Guid.NewGuid(),
                Kind: WorkflowNodeKind.Logic,
                AgentKey: null,
                AgentVersion: null,
                OutputScript: null,
                OutputPorts: new[] { "Yes", "No" },
                LayoutX: 0, LayoutY: 0),
        };

        var findings = await fx.RunRuleAsync(nodes);

        findings.Should().BeEmpty();
    }

    [Fact]
    public async Task SubflowAndReviewLoopNodes_Skipped()
    {
        // Subflow / ReviewLoop ports come from the child workflow, not from a pinned agent.
        await using var fx = await TestFixture.CreateAsync();
        var nodes = new[]
        {
            new WorkflowNodeDto(
                Id: Guid.NewGuid(),
                Kind: WorkflowNodeKind.Subflow,
                AgentKey: null,
                AgentVersion: null,
                OutputScript: null,
                OutputPorts: new[] { "Approved", "Cancelled" },
                LayoutX: 0, LayoutY: 0,
                SubflowKey: "child", SubflowVersion: 1),
            new WorkflowNodeDto(
                Id: Guid.NewGuid(),
                Kind: WorkflowNodeKind.ReviewLoop,
                AgentKey: null,
                AgentVersion: null,
                OutputScript: null,
                OutputPorts: new[] { "Approved", "Exhausted" },
                LayoutX: 0, LayoutY: 0,
                SubflowKey: "child", SubflowVersion: 1),
        };

        var findings = await fx.RunRuleAsync(nodes);

        findings.Should().BeEmpty();
    }

    [Fact]
    public async Task DuplicateAgentReference_LooksUpRepoOncePerVersion()
    {
        await using var fx = await TestFixture.CreateAsync();
        await fx.SeedAgentAsync("worker", new[] { "Done" });
        var nodes = new[]
        {
            AgentNode(Guid.NewGuid(), "worker", version: 1, ports: new[] { "Done" }),
            AgentNode(Guid.NewGuid(), "worker", version: 1, ports: new[] { "Done", "Other" }),
        };

        var findings = await fx.RunRuleAsync(nodes);

        // Second node has unwired-by-agent port — error on it. First node is clean.
        findings.Should().ContainSingle();
        findings[0].Severity.Should().Be(WorkflowValidationSeverity.Error);
        findings[0].Message.Should().Contain("'Other'");
    }

    private static WorkflowNodeDto AgentNode(
        Guid id,
        string agentKey,
        int? version,
        IReadOnlyList<string> ports) =>
        new(
            Id: id,
            Kind: WorkflowNodeKind.Agent,
            AgentKey: agentKey,
            AgentVersion: version,
            OutputScript: null,
            OutputPorts: ports,
            LayoutX: 0, LayoutY: 0);

    private sealed class TestFixture : IAsyncDisposable
    {
        private readonly CodeFlowDbContext db;
        private readonly IWorkflowRepository workflowRepo;
        private readonly IAgentConfigRepository agentRepo;
        private readonly PortCouplingRule rule;

        private TestFixture(
            CodeFlowDbContext db,
            IWorkflowRepository workflowRepo,
            IAgentConfigRepository agentRepo,
            IAgentRoleRepository roleRepo)
        {
            this.db = db;
            this.workflowRepo = workflowRepo;
            this.agentRepo = agentRepo;
            this.roleRepo = roleRepo;
            rule = new PortCouplingRule();
        }

        private readonly IAgentRoleRepository roleRepo;

        public static Task<TestFixture> CreateAsync()
        {
            // Static AgentConfig cache leaks between tests that reuse keys; reset before each
            // fixture so this test's fresh in-memory DB always wins the lookup.
            AgentConfigRepository.ClearCacheForTests();
            var options = new DbContextOptionsBuilder<CodeFlowDbContext>()
                .UseInMemoryDatabase($"port-coupling-tests-{Guid.NewGuid():N}")
                .Options;
            var ctx = new CodeFlowDbContext(options);
            return Task.FromResult(new TestFixture(
                ctx,
                new WorkflowRepository(ctx),
                new AgentConfigRepository(ctx),
                new AgentRoleRepository(ctx)));
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
            db.Agents.Add(new AgentConfigEntity
            {
                Key = key,
                Version = version,
                ConfigJson = configJson,
                CreatedAtUtc = DateTime.UtcNow,
                IsActive = true,
            });
            await db.SaveChangesAsync();
        }

        public async Task<IReadOnlyList<WorkflowValidationFinding>> RunRuleAsync(
            IReadOnlyList<WorkflowNodeDto> nodes)
        {
            var context = new WorkflowValidationContext(
                Key: "test-flow",
                Name: "Test flow",
                MaxRoundsPerRound: 3,
                Nodes: nodes,
                Edges: Array.Empty<WorkflowEdgeDto>(),
                Inputs: null,
                DbContext: db,
                WorkflowRepository: workflowRepo,
                AgentRepository: agentRepo,
                AgentRoleRepository: roleRepo);
            return await rule.RunAsync(context, CancellationToken.None);
        }

        public ValueTask DisposeAsync() => db.DisposeAsync();
    }
}
