using CodeFlow.Api.Dtos;
using CodeFlow.Api.Validation.Pipeline;
using CodeFlow.Api.Validation.Pipeline.Rules;
using CodeFlow.Orchestration.Scripting;
using CodeFlow.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace CodeFlow.Api.Tests.Validation;

/// <summary>
/// Tests for VZ2's <see cref="WorkflowVarDeclarationRule"/>: warns when a workflow opts in to
/// workflow-vars declarations but doesn't keep them in sync with what the agents read /
/// scripts write.
/// </summary>
public sealed class WorkflowVarDeclarationRuleTests
{
    [Fact]
    public async Task Skips_WhenNoDeclarationsProvided()
    {
        // CR1 contract: workflows without declarations behave identically to today.
        await using var fx = await TestFixture.CreateAsync();
        await fx.SeedAgentAsync("reviewer", systemPrompt: "Read {{ workflow.unmentioned }}");

        var findings = await fx.RunRuleAsync(
            nodes: new[] { AgentNode("reviewer", version: 1) },
            edges: Array.Empty<WorkflowEdgeDto>(),
            workflowVarsReads: null,
            workflowVarsWrites: null);

        findings.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadsDeclaredWithUpstreamWriter_NoFinding()
    {
        // Acceptance: a workflow declaring reads = [requestSummary] WITH an upstream writer
        // (here: kickoff sets workflow.requestSummary in its output script) saves clean.
        await using var fx = await TestFixture.CreateAsync();
        await fx.SeedAgentAsync("kickoff");
        await fx.SeedAgentAsync("reviewer",
            promptTemplate: "Reading {{ workflow.requestSummary }}");

        var startId = Guid.NewGuid();
        var reviewerId = Guid.NewGuid();
        var nodes = new[]
        {
            AgentStartNode(startId, "kickoff", outputScript: "setWorkflow('requestSummary', input.text);"),
            AgentNode("reviewer", reviewerId, version: 1),
        };
        var edges = new[]
        {
            new WorkflowEdgeDto(startId, "Continue", reviewerId, "in",
                RotatesRound: false, SortOrder: 0),
        };

        var findings = await fx.RunRuleAsync(
            nodes,
            edges,
            workflowVarsReads: new[] { "requestSummary" },
            workflowVarsWrites: new[] { "requestSummary" });

        findings.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadsDeclared_NoUpstreamWriter_AndNotInDeclaration_FiresWarning()
    {
        // Acceptance: a workflow declaring reads = [requestSummary] but with no upstream
        // writer warns at save.
        await using var fx = await TestFixture.CreateAsync();
        await fx.SeedAgentAsync("reviewer",
            promptTemplate: "Reading {{ workflow.ghostKey }}");

        var findings = await fx.RunRuleAsync(
            nodes: new[] { AgentNode("reviewer", version: 1) },
            edges: Array.Empty<WorkflowEdgeDto>(),
            workflowVarsReads: new[] { "differentKey" }, // ghostKey not declared, no writer
            workflowVarsWrites: null);

        findings.Should().ContainSingle();
        findings[0].RuleId.Should().Be("workflow-vars-declaration");
        findings[0].Severity.Should().Be(WorkflowValidationSeverity.Warning);
        findings[0].Message.Should().Contain("ghostKey");
    }

    [Fact]
    public async Task ReadsDeclared_KeyInDeclaration_NoFinding_EvenWithoutUpstreamWriter()
    {
        // The author can promise "this variable will be set externally" by including it in
        // the declaration. The validator trusts the declaration.
        await using var fx = await TestFixture.CreateAsync();
        await fx.SeedAgentAsync("reviewer",
            promptTemplate: "Reading {{ workflow.externalKey }}");

        var findings = await fx.RunRuleAsync(
            nodes: new[] { AgentNode("reviewer", version: 1) },
            edges: Array.Empty<WorkflowEdgeDto>(),
            workflowVarsReads: new[] { "externalKey" },
            workflowVarsWrites: null);

        findings.Should().BeEmpty();
    }

    [Fact]
    public async Task WritesDeclared_ScriptWritesUndeclaredKey_FiresWarning()
    {
        // Acceptance: an upstream node's script writes 'X' but X is not in the declaration —
        // surface a warning.
        await using var fx = await TestFixture.CreateAsync();
        await fx.SeedAgentAsync("kickoff");

        var findings = await fx.RunRuleAsync(
            nodes: new[] { AgentStartNode(Guid.NewGuid(), "kickoff",
                outputScript: "setWorkflow('undeclared', 'oops');") },
            edges: Array.Empty<WorkflowEdgeDto>(),
            workflowVarsReads: null,
            workflowVarsWrites: Array.Empty<string>()); // empty = explicit "writes nothing"

        findings.Should().ContainSingle();
        findings[0].Severity.Should().Be(WorkflowValidationSeverity.Warning);
        findings[0].Message.Should().Contain("undeclared");
        findings[0].Message.Should().Contain("setWorkflow");
    }

    [Fact]
    public async Task WritesDeclared_MirrorTargetUndeclared_FiresWarning()
    {
        // P4 integration: a node with MirrorOutputToWorkflowVar must be reflected in the
        // declared writes too.
        await using var fx = await TestFixture.CreateAsync();
        await fx.SeedAgentAsync("kickoff");

        var nodes = new[]
        {
            AgentStartNodeWithMirror(Guid.NewGuid(), "kickoff",
                mirrorOutputToWorkflowVar: "currentPlan"),
        };

        var findings = await fx.RunRuleAsync(
            nodes,
            edges: Array.Empty<WorkflowEdgeDto>(),
            workflowVarsReads: null,
            workflowVarsWrites: Array.Empty<string>());

        findings.Should().ContainSingle();
        findings[0].Message.Should().Contain("currentPlan");
        findings[0].Message.Should().Contain("mirrors output");
    }

    [Fact]
    public async Task ReservedFrameworkKeys_NeverFireFindings()
    {
        // traceWorkDir / traceId / __loop.* are framework-managed. Authors don't declare them.
        await using var fx = await TestFixture.CreateAsync();
        await fx.SeedAgentAsync("reviewer",
            promptTemplate: "{{ workflow.traceWorkDir }} {{ workflow.traceId }} {{ workflow.__loop }}");

        var findings = await fx.RunRuleAsync(
            nodes: new[] { AgentNode("reviewer", version: 1) },
            edges: Array.Empty<WorkflowEdgeDto>(),
            workflowVarsReads: Array.Empty<string>(),
            workflowVarsWrites: null);

        findings.Should().BeEmpty();
    }

    [Fact]
    public async Task ScriptWritesDeclaredKey_NoFinding()
    {
        await using var fx = await TestFixture.CreateAsync();
        await fx.SeedAgentAsync("kickoff");

        var findings = await fx.RunRuleAsync(
            nodes: new[] { AgentStartNode(Guid.NewGuid(), "kickoff",
                outputScript: "setWorkflow('plan', input.text);") },
            edges: Array.Empty<WorkflowEdgeDto>(),
            workflowVarsReads: null,
            workflowVarsWrites: new[] { "plan" });

        findings.Should().BeEmpty();
    }

    [Fact]
    public async Task ReferencedVariableThatStartsWithWorkflowReserved_DoesNotMatchPattern()
    {
        // Defensive: `{{ workflow.requestSummary }}` is a match; `{{ workflowSummary }}`
        // (no dot) is not. The pattern is anchored.
        await using var fx = await TestFixture.CreateAsync();
        await fx.SeedAgentAsync("reviewer",
            promptTemplate: "{{ workflowSummary }} something else");

        var findings = await fx.RunRuleAsync(
            nodes: new[] { AgentNode("reviewer", version: 1) },
            edges: Array.Empty<WorkflowEdgeDto>(),
            workflowVarsReads: Array.Empty<string>(),
            workflowVarsWrites: null);

        findings.Should().BeEmpty();
    }

    private static WorkflowNodeDto AgentNode(string agentKey, int? version) => new(
        Id: Guid.NewGuid(),
        Kind: WorkflowNodeKind.Agent,
        AgentKey: agentKey,
        AgentVersion: version,
        OutputScript: null,
        OutputPorts: new[] { "Done" },
        LayoutX: 0, LayoutY: 0);

    private static WorkflowNodeDto AgentNode(string agentKey, Guid id, int? version) => new(
        Id: id,
        Kind: WorkflowNodeKind.Agent,
        AgentKey: agentKey,
        AgentVersion: version,
        OutputScript: null,
        OutputPorts: new[] { "Done" },
        LayoutX: 0, LayoutY: 0);

    private static WorkflowNodeDto AgentStartNode(Guid id, string agentKey, string? outputScript) => new(
        Id: id,
        Kind: WorkflowNodeKind.Start,
        AgentKey: agentKey,
        AgentVersion: 1,
        OutputScript: outputScript,
        OutputPorts: new[] { "Continue" },
        LayoutX: 0, LayoutY: 0);

    private static WorkflowNodeDto AgentStartNodeWithMirror(Guid id, string agentKey, string mirrorOutputToWorkflowVar) =>
        new(
            Id: id,
            Kind: WorkflowNodeKind.Start,
            AgentKey: agentKey,
            AgentVersion: 1,
            OutputScript: null,
            OutputPorts: new[] { "Continue" },
            LayoutX: 0, LayoutY: 0,
            MirrorOutputToWorkflowVar: mirrorOutputToWorkflowVar);

    private sealed class TestFixture : IAsyncDisposable
    {
        private readonly CodeFlowDbContext db;
        private readonly IWorkflowRepository workflowRepo;
        private readonly IAgentConfigRepository agentRepo;
        private readonly IAgentRoleRepository roleRepo;
        private readonly WorkflowVarDeclarationRule rule;

        private TestFixture(
            CodeFlowDbContext db,
            IWorkflowRepository workflowRepo,
            IAgentConfigRepository agentRepo,
            IAgentRoleRepository roleRepo,
            WorkflowVarDeclarationRule rule)
        {
            this.db = db;
            this.workflowRepo = workflowRepo;
            this.agentRepo = agentRepo;
            this.roleRepo = roleRepo;
            this.rule = rule;
        }

        public static Task<TestFixture> CreateAsync()
        {
            AgentConfigRepository.ClearCacheForTests();
            var options = new DbContextOptionsBuilder<CodeFlowDbContext>()
                .UseInMemoryDatabase($"vz2-tests-{Guid.NewGuid():N}")
                .Options;
            var ctx = new CodeFlowDbContext(options);
            var analyzer = new WorkflowDataflowAnalyzer();
            return Task.FromResult(new TestFixture(
                ctx,
                new WorkflowRepository(ctx),
                new AgentConfigRepository(ctx),
                new AgentRoleRepository(ctx),
                new WorkflowVarDeclarationRule(analyzer)));
        }

        public async Task SeedAgentAsync(
            string key,
            string? systemPrompt = null,
            string? promptTemplate = null,
            int version = 1)
        {
            var promptField = systemPrompt is null
                ? string.Empty
                : $""", "systemPrompt": {System.Text.Json.JsonSerializer.Serialize(systemPrompt)}""";
            var templateField = promptTemplate is null
                ? string.Empty
                : $""", "promptTemplate": {System.Text.Json.JsonSerializer.Serialize(promptTemplate)}""";
            var configJson = $$"""
                {
                    "type": "agent",
                    "provider": "openai",
                    "model": "gpt-test"{{promptField}}{{templateField}}
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
            IReadOnlyList<WorkflowNodeDto> nodes,
            IReadOnlyList<WorkflowEdgeDto> edges,
            IReadOnlyList<string>? workflowVarsReads,
            IReadOnlyList<string>? workflowVarsWrites)
        {
            var context = new WorkflowValidationContext(
                Key: "test-flow",
                Name: "Test flow",
                MaxRoundsPerRound: 3,
                Nodes: nodes,
                Edges: edges,
                Inputs: null,
                DbContext: db,
                WorkflowRepository: workflowRepo,
                AgentRepository: agentRepo,
                AgentRoleRepository: roleRepo,
                WorkflowVarsReads: workflowVarsReads,
                WorkflowVarsWrites: workflowVarsWrites);
            return await rule.RunAsync(context, CancellationToken.None);
        }

        public ValueTask DisposeAsync() => db.DisposeAsync();
    }
}
