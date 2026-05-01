using CodeFlow.Api.Dtos;
using CodeFlow.Api.Validation.Pipeline;
using CodeFlow.Api.Validation.Pipeline.Rules;
using CodeFlow.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace CodeFlow.Api.Tests.Validation;

/// <summary>
/// Tests for V5's <see cref="RoleAssignmentRule"/>: warning when an agent has zero roles, and
/// error escalation when the agent's prompt mentions a host-tool / MCP capability.
/// </summary>
public sealed class RoleAssignmentRuleTests
{
    [Fact]
    public async Task ZeroRoles_PromptMentionsRunCommand_EmitsErrorPointingToRolesPage()
    {
        await using var fx = await TestFixture.CreateAsync();
        await fx.SeedAgentAsync("dev",
            systemPrompt: "You are a developer. Use `run_command` to clone the repo.");

        var nodes = new[] { AgentNode("dev", version: 1) };

        var findings = await fx.RunRuleAsync(nodes);

        findings.Should().ContainSingle();
        findings[0].Severity.Should().Be(WorkflowValidationSeverity.Error);
        findings[0].Message.Should().Contain("dev");
        findings[0].Message.Should().Contain("run_command");
        findings[0].Message.Should().Contain("Roles");
    }

    [Fact]
    public async Task ZeroRoles_PromptMentionsApplyPatch_EmitsError()
    {
        await using var fx = await TestFixture.CreateAsync();
        await fx.SeedAgentAsync("worker",
            promptTemplate: "Apply your changes via apply_patch.");

        var findings = await fx.RunRuleAsync(new[] { AgentNode("worker", version: 1) });

        findings.Should().ContainSingle()
            .Which.Severity.Should().Be(WorkflowValidationSeverity.Error);
    }

    [Fact]
    public async Task ZeroRoles_PromptMentionsWebFetch_EmitsError()
    {
        // sc-451: web_fetch is a host-tool capability; agents that mention it but have no
        // role grants should fail save with the same Roles-page nudge.
        await using var fx = await TestFixture.CreateAsync();
        await fx.SeedAgentAsync("doc-reader",
            systemPrompt: "Use web_fetch to read the official setup guide before suggesting an image.");

        var findings = await fx.RunRuleAsync(new[] { AgentNode("doc-reader", version: 1) });

        findings.Should().ContainSingle();
        findings[0].Severity.Should().Be(WorkflowValidationSeverity.Error);
        findings[0].Message.Should().Contain("web_fetch");
    }

    [Fact]
    public async Task ZeroRoles_PromptMentionsContainerRun_EmitsError()
    {
        // sc-450: container.run is a host-tool capability; agents that mention it but have no
        // role grants should fail save with the same Roles-page nudge as run_command.
        await using var fx = await TestFixture.CreateAsync();
        await fx.SeedAgentAsync("builder",
            systemPrompt: "Build the project with container.run on a docker.io image.");

        var findings = await fx.RunRuleAsync(new[] { AgentNode("builder", version: 1) });

        findings.Should().ContainSingle();
        findings[0].Severity.Should().Be(WorkflowValidationSeverity.Error);
        findings[0].Message.Should().Contain("container.run");
    }

    [Fact]
    public async Task ZeroRoles_PromptMentionsMcp_EmitsError()
    {
        await using var fx = await TestFixture.CreateAsync();
        await fx.SeedAgentAsync("kanban-bot",
            systemPrompt: "When asked, call mcp:kanban:list_work_items.");

        var findings = await fx.RunRuleAsync(new[] { AgentNode("kanban-bot", version: 1) });

        findings.Should().ContainSingle()
            .Which.Severity.Should().Be(WorkflowValidationSeverity.Error);
    }

    [Fact]
    public async Task ZeroRoles_PureTextPrompt_EmitsWarningOnly()
    {
        // Classifiers, PRD producers etc. legitimately have no tools — do not block save.
        await using var fx = await TestFixture.CreateAsync();
        await fx.SeedAgentAsync("classifier",
            systemPrompt: "Classify the input as Approved or Rejected. Output only the label.");

        var findings = await fx.RunRuleAsync(new[] { AgentNode("classifier", version: 1) });

        findings.Should().ContainSingle()
            .Which.Severity.Should().Be(WorkflowValidationSeverity.Warning);
    }

    [Fact]
    public async Task AgentHasRoleAssignment_NoFindings()
    {
        await using var fx = await TestFixture.CreateAsync();
        await fx.SeedAgentAsync("dev",
            systemPrompt: "Use run_command and apply_patch.");
        await fx.AssignRoleAsync("dev", "code-worker");

        var findings = await fx.RunRuleAsync(new[] { AgentNode("dev", version: 1) });

        findings.Should().BeEmpty();
    }

    [Fact]
    public async Task SameAgentReferencedByMultipleNodes_EmitsAtMostOneFinding()
    {
        // De-dupe so a single misconfigured agent doesn't flood the panel when wired into a
        // workflow at multiple points (e.g. inside a ReviewLoop's producer + finalizer slot).
        await using var fx = await TestFixture.CreateAsync();
        await fx.SeedAgentAsync("classifier",
            systemPrompt: "Classify input.");
        var nodes = new[]
        {
            AgentNode("classifier", version: 1),
            AgentNode("classifier", version: 1),
            AgentNode("classifier", version: 1),
        };

        var findings = await fx.RunRuleAsync(nodes);

        findings.Should().ContainSingle();
    }

    [Fact]
    public async Task UnknownAgent_DoesNotCrash_AndDoesNotEmitCapabilityError()
    {
        // An unknown agent ref is reported separately by the legacy validator. The role rule
        // still fires "missing role" Warning/Error based on assignments alone — but capability
        // detection requires a resolvable agent config, so we degrade to Warning when the
        // config can't be loaded.
        await using var fx = await TestFixture.CreateAsync();
        // No SeedAgentAsync; no AssignRoleAsync.

        var findings = await fx.RunRuleAsync(new[] { AgentNode("ghost", version: 1) });

        findings.Should().ContainSingle();
        findings[0].Severity.Should().Be(WorkflowValidationSeverity.Warning);
    }

    [Fact]
    public async Task LogicAndSubflowNodes_NotChecked()
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
            new WorkflowNodeDto(
                Id: Guid.NewGuid(),
                Kind: WorkflowNodeKind.Subflow,
                AgentKey: null, AgentVersion: null,
                OutputScript: null, OutputPorts: new[] { "Done" },
                LayoutX: 0, LayoutY: 0,
                SubflowKey: "child", SubflowVersion: 1),
        };

        var findings = await fx.RunRuleAsync(nodes);

        findings.Should().BeEmpty();
    }

    [Fact]
    public async Task CapabilityDetection_WordBoundary_DoesNotMatchSubstring()
    {
        // "read_files" or "rerun_command" should not trip the regex — guard against false-positive
        // errors that would block save unfairly.
        await using var fx = await TestFixture.CreateAsync();
        await fx.SeedAgentAsync("safe",
            systemPrompt: "Read files manually. Then rerun_command_history if needed.");

        var findings = await fx.RunRuleAsync(new[] { AgentNode("safe", version: 1) });

        findings.Should().ContainSingle()
            .Which.Severity.Should().Be(WorkflowValidationSeverity.Warning);
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
        private readonly RoleAssignmentRule rule;

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
            rule = new RoleAssignmentRule();
        }

        public static Task<TestFixture> CreateAsync()
        {
            // Static AgentConfig cache leaks between tests that reuse keys; reset before each
            // fixture so this test's fresh in-memory DB always wins the lookup.
            AgentConfigRepository.ClearCacheForTests();
            var options = new DbContextOptionsBuilder<CodeFlowDbContext>()
                .UseInMemoryDatabase($"role-rule-tests-{Guid.NewGuid():N}")
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

        public async Task AssignRoleAsync(string agentKey, string roleKey)
        {
            // Seed directly: AgentRoleRepository writes use a transaction, which the in-memory
            // provider doesn't support.
            var role = new AgentRoleEntity
            {
                Key = roleKey,
                DisplayName = roleKey,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
            };
            db.AgentRoles.Add(role);
            await db.SaveChangesAsync();
            db.AgentRoleAssignments.Add(new AgentRoleAssignmentEntity
            {
                AgentKey = agentKey,
                RoleId = role.Id,
                Role = role,
                CreatedAtUtc = DateTime.UtcNow,
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
