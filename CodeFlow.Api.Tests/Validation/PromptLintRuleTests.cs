using CodeFlow.Api.Dtos;
using CodeFlow.Api.Validation.Pipeline;
using CodeFlow.Api.Validation.Pipeline.Rules;
using CodeFlow.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace CodeFlow.Api.Tests.Validation;

/// <summary>
/// Tests for V7's <see cref="PromptLintRule"/>: forbidden-phrase detection on agent prompts.
/// </summary>
public sealed class PromptLintRuleTests
{
    [Fact]
    public async Task PromptContainsDefaultToRejected_FiresWarning()
    {
        // Spec acceptance: "default to Rejected" produces a lint warning.
        await using var fx = await TestFixture.CreateAsync();
        await fx.SeedAgentAsync("reviewer",
            systemPrompt: "You are a strict reviewer. When unsure, default to Rejected.");

        var findings = await fx.RunRuleAsync(new[] { AgentNode("reviewer", version: 1) });

        findings.Should().ContainSingle();
        findings[0].Severity.Should().Be(WorkflowValidationSeverity.Warning);
        findings[0].RuleId.Should().Be("prompt-lint");
        findings[0].Message.Should().Contain("reviewer");
        findings[0].Message.Should().Contain("default to Rejected");
        findings[0].Message.Should().Contain("@codeflow/reviewer-base");
    }

    [Fact]
    public async Task PromptContainsAlwaysReject_FiresWarning()
    {
        await using var fx = await TestFixture.CreateAsync();
        await fx.SeedAgentAsync("strict",
            promptTemplate: "Be very thorough. You must always reject changes that are not perfect.");

        var findings = await fx.RunRuleAsync(new[] { AgentNode("strict", version: 1) });

        findings.Should().ContainSingle();
        findings[0].Message.ToLowerInvariant().Should().Contain("you must always reject");
    }

    [Fact]
    public async Task PromptContainsIterationGoal_FiresWarning()
    {
        await using var fx = await TestFixture.CreateAsync();
        await fx.SeedAgentAsync("nagging",
            systemPrompt: "Push back on the producer. The goal is 5 iterations of feedback.");

        var findings = await fx.RunRuleAsync(new[] { AgentNode("nagging", version: 1) });

        findings.Should().ContainSingle();
        findings[0].Message.ToLowerInvariant().Should().Contain("the goal is 5 iterations");
    }

    [Fact]
    public async Task PromptContainsKeepIteratingUntil_FiresWarning()
    {
        await using var fx = await TestFixture.CreateAsync();
        await fx.SeedAgentAsync("loopy",
            systemPrompt: "Keep iterating until the producer surrenders.");

        var findings = await fx.RunRuleAsync(new[] { AgentNode("loopy", version: 1) });

        findings.Should().ContainSingle();
        findings[0].Message.ToLowerInvariant().Should().Contain("keep iterating until");
    }

    [Fact]
    public async Task CleanPrompt_NoFindings()
    {
        await using var fx = await TestFixture.CreateAsync();
        await fx.SeedAgentAsync("approve-when",
            systemPrompt: "Approve when the change includes tests, a clear description, and "
                + "passes the linter. Otherwise return Rejected with feedback.");

        var findings = await fx.RunRuleAsync(new[] { AgentNode("approve-when", version: 1) });

        findings.Should().BeEmpty();
    }

    [Fact]
    public async Task MultipleForbiddenPhrasesInOnePrompt_FiresOnePerPhrase()
    {
        await using var fx = await TestFixture.CreateAsync();
        await fx.SeedAgentAsync("worst",
            systemPrompt: "Default to Rejected and keep iterating until the producer fixes everything.");

        var findings = await fx.RunRuleAsync(new[] { AgentNode("worst", version: 1) });

        findings.Should().HaveCount(2);
        findings.Should().Contain(f => f.Message.Contains("default to Rejected", StringComparison.OrdinalIgnoreCase));
        findings.Should().Contain(f => f.Message.Contains("keep iterating until", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SamePhraseInBothPrompts_FiresOnceNotTwice()
    {
        await using var fx = await TestFixture.CreateAsync();
        await fx.SeedAgentAsync("duplicate",
            systemPrompt: "Default to Rejected.",
            promptTemplate: "Remember to default to Rejected.");

        var findings = await fx.RunRuleAsync(new[] { AgentNode("duplicate", version: 1) });

        findings.Should().ContainSingle();
    }

    [Fact]
    public async Task SameAgentReferencedByMultipleNodes_LintedOnce()
    {
        // De-dupe across nodes that share an agent — same as the role-assignment rule.
        await using var fx = await TestFixture.CreateAsync();
        await fx.SeedAgentAsync("nagging", systemPrompt: "Default to rejected.");
        var nodes = new[]
        {
            AgentNode("nagging", version: 1),
            AgentNode("nagging", version: 1),
        };

        var findings = await fx.RunRuleAsync(nodes);

        findings.Should().ContainSingle();
    }

    [Fact]
    public async Task LogicAndSubflowNodes_NotLinted()
    {
        await using var fx = await TestFixture.CreateAsync();
        var nodes = new[]
        {
            new WorkflowNodeDto(
                Id: Guid.NewGuid(),
                Kind: WorkflowNodeKind.Logic,
                AgentKey: null, AgentVersion: null,
                OutputScript: null, OutputPorts: new[] { "Yes" },
                LayoutX: 0, LayoutY: 0),
        };

        var findings = await fx.RunRuleAsync(nodes);

        findings.Should().BeEmpty();
    }

    [Fact]
    public async Task UnknownAgent_DoesNotCrash()
    {
        await using var fx = await TestFixture.CreateAsync();

        var findings = await fx.RunRuleAsync(new[] { AgentNode("ghost", version: 1) });

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

    private sealed class TestFixture : IAsyncDisposable
    {
        private readonly CodeFlowDbContext db;
        private readonly IWorkflowRepository workflowRepo;
        private readonly IAgentConfigRepository agentRepo;
        private readonly IAgentRoleRepository roleRepo;
        private readonly PromptLintRule rule;

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
            rule = new PromptLintRule();
        }

        public static Task<TestFixture> CreateAsync()
        {
            AgentConfigRepository.ClearCacheForTests();
            var options = new DbContextOptionsBuilder<CodeFlowDbContext>()
                .UseInMemoryDatabase($"prompt-lint-tests-{Guid.NewGuid():N}")
                .Options;
            var ctx = new CodeFlowDbContext(options);
            return Task.FromResult(new TestFixture(
                ctx,
                new WorkflowRepository(ctx),
                new AgentConfigRepository(ctx),
                new AgentRoleRepository(ctx)));
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
