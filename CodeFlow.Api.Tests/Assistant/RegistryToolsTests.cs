using System.Text.Json;
using CodeFlow.Api.Assistant.Tools;
using CodeFlow.Api.Tests.Integration;
using CodeFlow.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodeFlow.Api.Tests.Assistant;

/// <summary>
/// Integration tests for HAA-4 read-only registry tools. Each test seeds a tiny fixture (one or
/// two workflows / agents / roles) directly via DbContext, then invokes the corresponding tool
/// through the live DI container and asserts on the JSON result.
/// </summary>
[Trait("Category", "EndToEnd")]
public sealed class RegistryToolsTests : IClassFixture<CodeFlowApiFactory>, IAsyncLifetime
{
    private readonly CodeFlowApiFactory factory;

    public RegistryToolsTests(CodeFlowApiFactory factory)
    {
        this.factory = factory;
    }

    public async Task InitializeAsync()
    {
        // Each test runs against a clean catalog. Workflow / Agent rows survive across tests
        // otherwise because the API factory is a class fixture — a single MariaDB database for
        // the whole test class.
        AgentConfigRepository.ClearCacheForTests();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();
        db.AssistantMessages.RemoveRange(db.AssistantMessages);
        db.AssistantConversations.RemoveRange(db.AssistantConversations);
        db.WorkflowEdges.RemoveRange(db.WorkflowEdges);
        db.WorkflowNodes.RemoveRange(db.WorkflowNodes);
        db.WorkflowInputs.RemoveRange(db.WorkflowInputs);
        db.Workflows.RemoveRange(db.Workflows);
        db.Agents.RemoveRange(db.Agents);
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly JsonElement EmptyArgs = JsonDocument.Parse("{}").RootElement;

    private static JsonElement Args(object obj) =>
        JsonSerializer.SerializeToElement(obj);

    [Fact]
    public async Task ListWorkflows_FiltersByCategoryAndNamePrefix()
    {
        await SeedWorkflowAsync("alpha-flow", name: "Alpha", category: WorkflowCategory.Workflow);
        await SeedWorkflowAsync("beta-sub", name: "Beta sub", category: WorkflowCategory.Subflow);
        await SeedWorkflowAsync("alpha-loop", name: "Alpha loop", category: WorkflowCategory.Loop);

        await using var scope = factory.Services.CreateAsyncScope();
        var tool = ResolveTool<ListWorkflowsTool>(scope);

        var allResult = ParseObject(await tool.InvokeAsync(EmptyArgs, CancellationToken.None));
        allResult.GetProperty("count").GetInt32().Should().Be(3);

        var subflowsOnly = ParseObject(await tool.InvokeAsync(Args(new { category = "Subflow" }), CancellationToken.None));
        subflowsOnly.GetProperty("count").GetInt32().Should().Be(1);
        subflowsOnly.GetProperty("workflows")[0].GetProperty("key").GetString().Should().Be("beta-sub");

        var alphaOnly = ParseObject(await tool.InvokeAsync(Args(new { namePrefix = "Alpha" }), CancellationToken.None));
        alphaOnly.GetProperty("count").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task GetWorkflow_LatestVersion_ReturnsFullGraph()
    {
        var v1 = await SeedWorkflowAsync("evolving", name: "Evolving v1");
        v1.Should().Be(1);
        var v2 = await SeedWorkflowAsync("evolving", name: "Evolving v2");
        v2.Should().Be(2);

        await using var scope = factory.Services.CreateAsyncScope();
        var tool = ResolveTool<GetWorkflowTool>(scope);

        var latest = ParseObject(await tool.InvokeAsync(Args(new { key = "evolving" }), CancellationToken.None));
        latest.GetProperty("version").GetInt32().Should().Be(2);
        latest.GetProperty("name").GetString().Should().Be("Evolving v2");
        latest.GetProperty("nodes").GetArrayLength().Should().BeGreaterThan(0);

        var pinned = ParseObject(await tool.InvokeAsync(Args(new { key = "evolving", version = 1 }), CancellationToken.None));
        pinned.GetProperty("version").GetInt32().Should().Be(1);
        pinned.GetProperty("name").GetString().Should().Be("Evolving v1");
    }

    [Fact]
    public async Task GetWorkflow_NotFound_ReturnsErrorResult()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var tool = ResolveTool<GetWorkflowTool>(scope);

        var result = await tool.InvokeAsync(Args(new { key = "does-not-exist" }), CancellationToken.None);
        result.IsError.Should().BeTrue();
        result.ResultJson.Should().Contain("not found");
    }

    [Fact]
    public async Task ListWorkflowVersions_ReturnsNewestFirst()
    {
        await SeedWorkflowAsync("multi", name: "First");
        await SeedWorkflowAsync("multi", name: "Second");

        await using var scope = factory.Services.CreateAsyncScope();
        var tool = ResolveTool<ListWorkflowVersionsTool>(scope);

        var result = ParseObject(await tool.InvokeAsync(Args(new { key = "multi" }), CancellationToken.None));
        result.GetProperty("count").GetInt32().Should().Be(2);
        var versions = result.GetProperty("versions");
        versions[0].GetProperty("version").GetInt32().Should().Be(2);
        versions[1].GetProperty("version").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task ListAgents_FiltersByProviderAndExcludesForks()
    {
        await SeedAgentAsync("anth-1", provider: "anthropic", model: "claude-sonnet-4");
        await SeedAgentAsync("oa-1", provider: "openai", model: "gpt-4");
        await SeedAgentAsync("__fork_xxx", provider: "anthropic", model: "claude-sonnet-4", owningWorkflowKey: "scoped");

        await using var scope = factory.Services.CreateAsyncScope();
        var tool = ResolveTool<ListAgentsTool>(scope);

        var all = ParseObject(await tool.InvokeAsync(EmptyArgs, CancellationToken.None));
        all.GetProperty("count").GetInt32().Should().Be(2); // fork excluded

        var anthOnly = ParseObject(await tool.InvokeAsync(Args(new { provider = "anthropic" }), CancellationToken.None));
        anthOnly.GetProperty("count").GetInt32().Should().Be(1);
        anthOnly.GetProperty("agents")[0].GetProperty("key").GetString().Should().Be("anth-1");
    }

    [Fact]
    public async Task GetAgent_ReturnsConfigWithTruncatedPrompts()
    {
        var longPrompt = new string('p', 5000);
        await SeedAgentAsync("verbose", provider: "anthropic", model: "claude-sonnet-4", systemPrompt: longPrompt);

        await using var scope = factory.Services.CreateAsyncScope();
        var tool = ResolveTool<GetAgentTool>(scope);

        var result = ParseObject(await tool.InvokeAsync(Args(new { key = "verbose" }), CancellationToken.None));
        result.GetProperty("provider").GetString().Should().Be("anthropic");
        var systemPrompt = result.GetProperty("systemPrompt").GetString()!;
        systemPrompt.Should().Contain("[truncated"); // 5000 > 4096 cap
        systemPrompt.Length.Should().BeLessThan(5000);
    }

    [Fact]
    public async Task FindWorkflowsUsingAgent_MatchesAgentSlot()
    {
        await SeedAgentAsync("hot-agent", provider: "anthropic", model: "claude-sonnet-4");
        await SeedWorkflowAsync("uses-hot", name: "Uses hot", agentKeyForFirstNode: "hot-agent", agentVersion: 1);
        await SeedWorkflowAsync("does-not", name: "Does not", agentKeyForFirstNode: "other-agent", agentVersion: 1);

        await using var scope = factory.Services.CreateAsyncScope();
        var tool = ResolveTool<FindWorkflowsUsingAgentTool>(scope);

        var result = ParseObject(await tool.InvokeAsync(Args(new { agentKey = "hot-agent" }), CancellationToken.None));
        result.GetProperty("count").GetInt32().Should().Be(1);
        result.GetProperty("workflows")[0].GetProperty("key").GetString().Should().Be("uses-hot");
        var matches = result.GetProperty("workflows")[0].GetProperty("matches");
        matches.GetArrayLength().Should().Be(1);
        matches[0].GetProperty("slot").GetString().Should().Be("AgentKey");
    }

    [Fact]
    public async Task SearchPrompts_FindsCaseInsensitiveSubstring_WithSnippet()
    {
        await SeedAgentAsync("a-1", provider: "anthropic", model: "m", systemPrompt: "You are a HELPFUL assistant.");
        await SeedAgentAsync("a-2", provider: "anthropic", model: "m", promptTemplate: "Please be helpful when answering.");
        await SeedAgentAsync("a-3", provider: "anthropic", model: "m", systemPrompt: "Unrelated content.");

        await using var scope = factory.Services.CreateAsyncScope();
        var tool = ResolveTool<SearchPromptsTool>(scope);

        var result = ParseObject(await tool.InvokeAsync(Args(new { query = "helpful" }), CancellationToken.None));
        result.GetProperty("count").GetInt32().Should().Be(2);

        var hitKeys = result.GetProperty("hits")
            .EnumerateArray()
            .Select(h => h.GetProperty("key").GetString())
            .OrderBy(k => k)
            .ToArray();
        hitKeys.Should().BeEquivalentTo(new[] { "a-1", "a-2" });
    }

    [Fact]
    public async Task GetAgentRole_ReturnsGrantsAndSkillNames()
    {
        // Seed a role with a host grant, an MCP grant, and one skill grant — exactly the surface
        // the assistant needs to self-diagnose what an assigned role permits.
        long roleId;
        long skillId;
        await using (var seedScope = factory.Services.CreateAsyncScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();
            var skill = new SkillEntity
            {
                Name = "test-skill",
                Body = "skill body",
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
            };
            db.Skills.Add(skill);
            await db.SaveChangesAsync();
            skillId = skill.Id;

            var role = new AgentRoleEntity
            {
                Key = "gar-test-role",
                DisplayName = "GetAgentRole test",
                Description = "test role",
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
            };
            db.AgentRoles.Add(role);
            await db.SaveChangesAsync();
            roleId = role.Id;

            db.AgentRoleToolGrants.AddRange(
                new AgentRoleToolGrantEntity
                {
                    RoleId = roleId,
                    Category = AgentRoleToolCategory.Host,
                    ToolIdentifier = "read_file",
                },
                new AgentRoleToolGrantEntity
                {
                    RoleId = roleId,
                    Category = AgentRoleToolCategory.Mcp,
                    ToolIdentifier = "mcp:codegraph:search_graph",
                });
            db.AgentRoleSkillGrants.Add(new AgentRoleSkillGrantEntity
            {
                RoleId = roleId,
                SkillId = skillId,
            });
            await db.SaveChangesAsync();
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var tool = ResolveTool<GetAgentRoleTool>(scope);

        // Lookup by id.
        var byId = ParseObject(await tool.InvokeAsync(Args(new { id = roleId }), CancellationToken.None));
        byId.GetProperty("key").GetString().Should().Be("gar-test-role");
        byId.GetProperty("grantCount").GetInt32().Should().Be(2);
        byId.GetProperty("skillCount").GetInt32().Should().Be(1);
        byId.GetProperty("skillNames")[0].GetString().Should().Be("test-skill");

        var grants = byId.GetProperty("toolGrants");
        grants.GetArrayLength().Should().Be(2);
        var grantTuples = grants.EnumerateArray()
            .Select(g => (g.GetProperty("category").GetString(), g.GetProperty("toolIdentifier").GetString()))
            .OrderBy(t => t.Item1)
            .ThenBy(t => t.Item2)
            .ToArray();
        grantTuples.Should().BeEquivalentTo(new[]
        {
            ("Host", "read_file"),
            ("Mcp", "mcp:codegraph:search_graph"),
        });

        // Lookup by key returns the same payload.
        var byKey = ParseObject(await tool.InvokeAsync(Args(new { key = "gar-test-role" }), CancellationToken.None));
        byKey.GetProperty("id").GetInt64().Should().Be(roleId);
        byKey.GetProperty("grantCount").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task GetAgentRole_RequiresExactlyOneOfIdOrKey()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var tool = ResolveTool<GetAgentRoleTool>(scope);

        var neither = await tool.InvokeAsync(EmptyArgs, CancellationToken.None);
        neither.IsError.Should().BeTrue();

        var both = await tool.InvokeAsync(Args(new { id = 1, key = "x" }), CancellationToken.None);
        both.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task GetAgentRole_NotFound_ReturnsErrorResult()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var tool = ResolveTool<GetAgentRoleTool>(scope);

        var result = await tool.InvokeAsync(Args(new { key = "no-such-role-xyz" }), CancellationToken.None);
        result.IsError.Should().BeTrue();
        result.ResultJson.Should().Contain("not found");
    }

    [Fact]
    public async Task ListAgentRoles_DefaultExcludesArchived()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var tool = ResolveTool<ListAgentRolesTool>(scope);

        // The system seeder runs at startup; just verify the tool returns SOME roles and an
        // includeArchived toggle changes the count or matches it (depending on whether any
        // archived rows exist in the seeded set).
        var defaultResult = ParseObject(await tool.InvokeAsync(EmptyArgs, CancellationToken.None));
        var withArchived = ParseObject(await tool.InvokeAsync(Args(new { includeArchived = true }), CancellationToken.None));

        defaultResult.GetProperty("count").GetInt32().Should().BeGreaterThan(0);
        withArchived.GetProperty("count").GetInt32()
            .Should().BeGreaterThanOrEqualTo(defaultResult.GetProperty("count").GetInt32());
    }

    private async Task<int> SeedWorkflowAsync(
        string key,
        string name,
        WorkflowCategory category = WorkflowCategory.Workflow,
        string? agentKeyForFirstNode = null,
        int? agentVersion = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWorkflowRepository>();

        var startNodeId = Guid.NewGuid();
        var agentNodeId = Guid.NewGuid();

        var nodes = new List<WorkflowNodeDraft>
        {
            new(
                Id: startNodeId,
                Kind: WorkflowNodeKind.Start,
                AgentKey: null,
                AgentVersion: null,
                OutputScript: null,
                OutputPorts: new[] { "Default" },
                LayoutX: 0,
                LayoutY: 0),
            new(
                Id: agentNodeId,
                Kind: WorkflowNodeKind.Agent,
                AgentKey: agentKeyForFirstNode,
                AgentVersion: agentVersion,
                OutputScript: null,
                OutputPorts: new[] { "Done" },
                LayoutX: 100,
                LayoutY: 0),
        };

        var edges = new[]
        {
            new WorkflowEdgeDraft(startNodeId, "Default", agentNodeId, "in", false, 0)
        };

        var draft = new WorkflowDraft(
            Key: key,
            Name: name,
            MaxRoundsPerRound: 3,
            Nodes: nodes,
            Edges: edges,
            Inputs: Array.Empty<WorkflowInputDraft>(),
            Category: category);

        return await repo.CreateNewVersionAsync(draft);
    }

    private async Task SeedAgentAsync(
        string key,
        string provider,
        string model,
        string? systemPrompt = null,
        string? promptTemplate = null,
        string? owningWorkflowKey = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();

        var configJson = JsonSerializer.Serialize(new
        {
            type = "agent",
            provider,
            model,
            systemPrompt,
            promptTemplate,
        });

        db.Agents.Add(new AgentConfigEntity
        {
            Key = key,
            Version = 1,
            ConfigJson = configJson,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = "test",
            IsActive = true,
            OwningWorkflowKey = owningWorkflowKey,
        });
        await db.SaveChangesAsync();
    }

    private static T ResolveTool<T>(AsyncServiceScope scope) where T : IAssistantTool
    {
        var tools = scope.ServiceProvider.GetServices<IAssistantTool>().OfType<T>().ToArray();
        tools.Should().HaveCount(1, $"exactly one {typeof(T).Name} should be registered");
        return tools[0];
    }

    private static JsonElement ParseObject(AssistantToolResult result)
    {
        result.IsError.Should().BeFalse(because: result.ResultJson);
        using var doc = JsonDocument.Parse(result.ResultJson);
        return doc.RootElement.Clone();
    }
}
