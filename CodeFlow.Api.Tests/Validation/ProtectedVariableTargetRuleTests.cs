using CodeFlow.Api.Dtos;
using CodeFlow.Api.Validation.Pipeline;
using CodeFlow.Api.Validation.Pipeline.Rules;
using CodeFlow.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace CodeFlow.Api.Tests.Validation;

/// <summary>
/// Tests for the P4/P5 follow-on <see cref="ProtectedVariableTargetRule"/>: surface workflow
/// nodes whose mirror or per-port-replacement targets land in framework-managed reserved
/// namespaces (today, <c>__loop.*</c>, <c>traceWorkDir</c>, <c>traceId</c>).
/// </summary>
public sealed class ProtectedVariableTargetRuleTests
{
    [Fact]
    public async Task MirrorTargetsLoopNamespace_FiresError()
    {
        // The runtime swallows mirrors targeting __loop.*; the editor must catch this at save
        // time so the author isn't left wondering why the variable never appears.
        await using var fx = await TestFixture.CreateAsync();
        var node = AgentNode("architect", mirrorOutputToWorkflowVar: "__loop.rejectionHistory");

        var findings = await fx.RunRuleAsync(new[] { node });

        findings.Should().ContainSingle();
        findings[0].RuleId.Should().Be("protected-variable-target");
        findings[0].Severity.Should().Be(WorkflowValidationSeverity.Error);
        findings[0].Message.Should().Contain("__loop.rejectionHistory");
        findings[0].Location?.NodeId.Should().Be(node.Id);
    }

    [Fact]
    public async Task MirrorTargetsTraceWorkDirOrTraceId_FiresError()
    {
        await using var fx = await TestFixture.CreateAsync();
        var traceWorkDirNode = AgentNode("a1", mirrorOutputToWorkflowVar: "traceWorkDir");
        var traceIdNode = AgentNode("a2", mirrorOutputToWorkflowVar: "traceId");

        var findings = await fx.RunRuleAsync(new[] { traceWorkDirNode, traceIdNode });

        findings.Should().HaveCount(2);
        findings.Should().AllSatisfy(f => f.Severity.Should().Be(WorkflowValidationSeverity.Error));
    }

    [Fact]
    public async Task MirrorTargetsUserKey_NoFinding()
    {
        await using var fx = await TestFixture.CreateAsync();
        var node = AgentNode("architect", mirrorOutputToWorkflowVar: "currentPlan");

        var findings = await fx.RunRuleAsync(new[] { node });

        findings.Should().BeEmpty();
    }

    [Fact]
    public async Task PortReplacementTargetsLoopNamespace_FiresError()
    {
        await using var fx = await TestFixture.CreateAsync();
        var node = AgentNode("reviewer", outputPortReplacements: new Dictionary<string, string>
        {
            ["Approved"] = "__loop.rejectionHistory",
        });

        var findings = await fx.RunRuleAsync(new[] { node });

        findings.Should().ContainSingle();
        findings[0].Severity.Should().Be(WorkflowValidationSeverity.Error);
        findings[0].Message.Should().Contain("Approved");
        findings[0].Message.Should().Contain("__loop.rejectionHistory");
    }

    [Fact]
    public async Task PortReplacementTargetsMixOfReservedAndUserKeys_FiresPerReservedTarget()
    {
        // One bad target per port — the rule emits one finding per misconfigured port so the
        // editor can surface them on the corresponding port chip.
        await using var fx = await TestFixture.CreateAsync();
        var node = AgentNode("reviewer", outputPortReplacements: new Dictionary<string, string>
        {
            ["Approved"] = "currentPlan",            // OK
            ["Rejected"] = "__loop.rejectionHistory", // bad
            ["Failed"] = "traceWorkDir",              // bad
        });

        var findings = await fx.RunRuleAsync(new[] { node });

        findings.Should().HaveCount(2);
        findings.Should().AllSatisfy(f => f.Severity.Should().Be(WorkflowValidationSeverity.Error));
        findings.Should().Contain(f => f.Message.Contains("Rejected"));
        findings.Should().Contain(f => f.Message.Contains("Failed"));
    }

    [Fact]
    public async Task NoConfiguration_NoFindings()
    {
        await using var fx = await TestFixture.CreateAsync();
        var node = AgentNode("plain");

        var findings = await fx.RunRuleAsync(new[] { node });

        findings.Should().BeEmpty();
    }

    private static WorkflowNodeDto AgentNode(
        string agentKey,
        string? mirrorOutputToWorkflowVar = null,
        IReadOnlyDictionary<string, string>? outputPortReplacements = null) => new(
        Id: Guid.NewGuid(),
        Kind: WorkflowNodeKind.Agent,
        AgentKey: agentKey,
        AgentVersion: 1,
        OutputScript: null,
        OutputPorts: new[] { "Approved", "Rejected", "Failed" },
        LayoutX: 0, LayoutY: 0,
        MirrorOutputToWorkflowVar: mirrorOutputToWorkflowVar,
        OutputPortReplacements: outputPortReplacements);

    private sealed class TestFixture : IAsyncDisposable
    {
        private readonly CodeFlowDbContext db;
        private readonly IWorkflowRepository workflowRepo;
        private readonly IAgentConfigRepository agentRepo;
        private readonly IAgentRoleRepository roleRepo;
        private readonly ProtectedVariableTargetRule rule;

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
            rule = new ProtectedVariableTargetRule();
        }

        public static Task<TestFixture> CreateAsync()
        {
            var options = new DbContextOptionsBuilder<CodeFlowDbContext>()
                .UseInMemoryDatabase($"protected-var-target-tests-{Guid.NewGuid():N}")
                .Options;
            var ctx = new CodeFlowDbContext(options);
            return Task.FromResult(new TestFixture(
                ctx,
                new WorkflowRepository(ctx),
                new AgentConfigRepository(ctx),
                new AgentRoleRepository(ctx)));
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
