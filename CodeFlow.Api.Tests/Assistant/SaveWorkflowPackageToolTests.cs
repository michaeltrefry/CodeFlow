using System.Text.Json;
using System.Text.Json.Nodes;
using CodeFlow.Api.Assistant.Tools;
using CodeFlow.Api.Tests.Integration;
using CodeFlow.Api.WorkflowPackages;
using CodeFlow.Persistence;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CodeFlow.Api.Tests.Assistant;

/// <summary>
/// Integration tests for HAA-10's `save_workflow_package` tool. The tool itself does not mutate;
/// it runs <see cref="IWorkflowPackageImporter.PreviewAsync"/> for self-containment validation
/// and returns the verdict for the chat UI to render as a confirmation chip. We verify the three
/// branches: malformed args (error), unresolvable package (status=invalid + missingReferences),
/// and a valid round-trippable package (status=preview_ok with item counts).
/// </summary>
[Trait("Category", "EndToEnd")]
public sealed class SaveWorkflowPackageToolTests : IClassFixture<CodeFlowApiFactory>, IAsyncLifetime
{
    private readonly CodeFlowApiFactory factory;

    public SaveWorkflowPackageToolTests(CodeFlowApiFactory factory)
    {
        this.factory = factory;
    }

    public async Task InitializeAsync()
    {
        AgentConfigRepository.ClearCacheForTests();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();
        db.WorkflowEdges.RemoveRange(db.WorkflowEdges);
        db.WorkflowNodes.RemoveRange(db.WorkflowNodes);
        db.WorkflowInputs.RemoveRange(db.WorkflowInputs);
        db.Workflows.RemoveRange(db.Workflows);
        db.Agents.RemoveRange(db.Agents);
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Invoke_WithoutPackageArgument_ReturnsError()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var tool = scope.ServiceProvider
            .GetRequiredService<IEnumerable<IAssistantTool>>()
            .OfType<SaveWorkflowPackageTool>()
            .Single();

        var result = await tool.InvokeAsync(JsonDocument.Parse("{}").RootElement, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.ResultJson.Should().Contain("package");
    }

    [Fact]
    public async Task Invoke_WithRefThatExistsNeitherInPackageNorDb_ReturnsPreviewConflicts()
    {
        // A workflow node references an agent that's neither embedded nor in the target library.
        // The validator no longer treats this as a structural rejection — the planner emits a
        // Conflict plan item so the LLM can see which specific ref failed to resolve.
        var package = BuildPackage(
            entryKey: "lonely-flow",
            entryVersion: 1,
            agentKeyForFirstNode: "missing-agent",
            agentVersion: 7,
            includeAgentInPackage: false);
        var args = JsonSerializer.SerializeToElement(new { package = JsonSerializer.SerializeToElement(package) });

        await using var scope = factory.Services.CreateAsyncScope();
        var tool = scope.ServiceProvider
            .GetRequiredService<IEnumerable<IAssistantTool>>()
            .OfType<SaveWorkflowPackageTool>()
            .Single();

        var result = await tool.InvokeAsync(args, CancellationToken.None);

        result.IsError.Should().BeFalse(
            because: "a conflict-laden preview is a reportable verdict for the LLM, not a tool failure");

        var parsed = JsonDocument.Parse(result.ResultJson).RootElement;
        parsed.GetProperty("status").GetString().Should().Be("preview_conflicts");
        parsed.GetProperty("canApply").GetBoolean().Should().BeFalse();
        parsed.GetProperty("conflictCount").GetInt32().Should().BeGreaterThan(0);

        var conflictMessages = parsed.GetProperty("items").EnumerateArray()
            .Where(item => item.GetProperty("action").GetString() == "Conflict")
            .Select(item => item.GetProperty("message").GetString() ?? string.Empty)
            .ToArray();
        conflictMessages.Should().Contain(message => message != null && message.Contains("missing-agent"),
            because: "the LLM must see which agent failed to resolve so it can fix the package");
    }

    [Fact]
    public async Task Invoke_WithRefOmittedButExistsInDb_ReturnsPreviewOkWithReuse()
    {
        // The assistant's common case after a get_workflow_package call: it embeds the new
        // workflow but doesn't re-embed the existing agent (since the truncated tool result
        // can't reproduce the agent's full system prompt verbatim). The importer must resolve
        // the unembedded ref against the DB and treat it as Reuse.
        const string agentKey = "haa10-resolver-fallback-writer";
        await SeedAgentAsync(agentKey);

        var package = BuildPackage(
            entryKey: "lonely-flow",
            entryVersion: 1,
            agentKeyForFirstNode: agentKey,
            agentVersion: 1,
            includeAgentInPackage: false);
        var args = JsonSerializer.SerializeToElement(new { package = JsonSerializer.SerializeToElement(package) });

        await using var scope = factory.Services.CreateAsyncScope();
        var tool = scope.ServiceProvider
            .GetRequiredService<IEnumerable<IAssistantTool>>()
            .OfType<SaveWorkflowPackageTool>()
            .Single();

        var result = await tool.InvokeAsync(args, CancellationToken.None);

        result.IsError.Should().BeFalse();
        var parsed = JsonDocument.Parse(result.ResultJson).RootElement;
        parsed.GetProperty("status").GetString().Should().Be("preview_ok");
        parsed.GetProperty("canApply").GetBoolean().Should().BeTrue();
        parsed.GetProperty("reuseCount").GetInt32().Should().BeGreaterThan(0,
            because: "the existing agent should have been resolved from the DB and reported as Reuse");

        var reuseItems = parsed.GetProperty("items").EnumerateArray()
            .Where(item => item.GetProperty("action").GetString() == "Reuse"
                && item.GetProperty("kind").GetString() == "Agent")
            .ToArray();
        reuseItems.Should().Contain(item => item.GetProperty("key").GetString() == agentKey,
            because: "the unembedded agent ref should appear as a DB-resolved Reuse item");
    }

    [Fact]
    public async Task Invoke_WithStringEnumNames_AcceptsLlmCanonicalShape()
    {
        // Regression: the assistant tool dispatcher previously used a serializer without
        // JsonStringEnumConverter while the HTTP /package/apply endpoint and every tool result
        // (get_workflow / get_agent) emitted enum names as strings. The LLM saw "Workflow",
        // "Agent", "Text" everywhere and reasonably mirrored that vocabulary, only to have
        // save_workflow_package reject it. We feed in a package serialized with explicit string
        // enum names to lock in that the dispatcher accepts what every other tool produces.
        const string agentKey = "haa10-string-enum-writer";
        await SeedAgentAsync(agentKey);
        await SeedWorkflowAsync("haa10-string-enum-flow", agentKey: agentKey, agentVersion: 1);

        WorkflowPackage package;
        await using (var seedScope = factory.Services.CreateAsyncScope())
        {
            var resolver = seedScope.ServiceProvider.GetRequiredService<IWorkflowPackageResolver>();
            package = await resolver.ResolveAsync("haa10-string-enum-flow", 1);
        }

        // Mimic what the LLM produces: camelCase property names (the assistant tool dispatcher
        // and HTTP API both expose camelCase JSON) plus string enum names.
        var llmStyleOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        };
        var packageElement = JsonSerializer.SerializeToElement(package, llmStyleOptions);

        // Sanity: the serialized package really does carry string enum names — otherwise the test
        // would be tautological (numeric integers always deserialized fine).
        var firstNodeKind = packageElement
            .GetProperty("workflows")[0]
            .GetProperty("nodes")[0]
            .GetProperty("kind");
        firstNodeKind.ValueKind.Should().Be(JsonValueKind.String,
            because: "the test must feed the dispatcher the LLM-canonical string form");

        var args = JsonSerializer.SerializeToElement(new { package = packageElement });

        await using var scope = factory.Services.CreateAsyncScope();
        var tool = scope.ServiceProvider
            .GetRequiredService<IEnumerable<IAssistantTool>>()
            .OfType<SaveWorkflowPackageTool>()
            .Single();

        var result = await tool.InvokeAsync(args, CancellationToken.None);

        result.IsError.Should().BeFalse(
            because: "string enum names are the LLM-canonical form and must round-trip");
        var parsed = JsonDocument.Parse(result.ResultJson).RootElement;
        parsed.GetProperty("status").GetString().Should().Be("preview_ok");
        parsed.GetProperty("canApply").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Invoke_WithSelfContainedPackage_ReturnsPreviewOk()
    {
        // Use the resolver against a real seeded workflow + agent so the package is self-
        // contained by construction. The tool then validates it via PreviewAsync.
        const string agentKey = "haa10-writer";
        await SeedAgentAsync(agentKey);
        await SeedWorkflowAsync("haa10-flow", agentKey: agentKey, agentVersion: 1);

        WorkflowPackage package;
        await using (var seedScope = factory.Services.CreateAsyncScope())
        {
            var resolver = seedScope.ServiceProvider.GetRequiredService<IWorkflowPackageResolver>();
            package = await resolver.ResolveAsync("haa10-flow", 1);
        }

        var args = JsonSerializer.SerializeToElement(new
        {
            package = JsonSerializer.SerializeToElement(package),
            note = "from-test",
        });

        await using var scope = factory.Services.CreateAsyncScope();
        var tool = scope.ServiceProvider
            .GetRequiredService<IEnumerable<IAssistantTool>>()
            .OfType<SaveWorkflowPackageTool>()
            .Single();

        var result = await tool.InvokeAsync(args, CancellationToken.None);

        result.IsError.Should().BeFalse();
        var parsed = JsonDocument.Parse(result.ResultJson).RootElement;
        parsed.GetProperty("status").GetString().Should().Be("preview_ok");
        parsed.GetProperty("canApply").GetBoolean().Should().BeTrue();
        parsed.GetProperty("entryPoint").GetProperty("key").GetString().Should().Be("haa10-flow");
        parsed.GetProperty("entryPoint").GetProperty("version").GetInt32().Should().Be(1);
        parsed.GetProperty("note").GetString().Should().Be("from-test");
        // The seeded workflow is already in the library, so the importer should report Reuse not
        // Create. (We're "saving" a package that already matches what's stored — the assistant's
        // common case during refinement.)
        parsed.GetProperty("reuseCount").GetInt32().Should().BeGreaterThan(0);
        parsed.GetProperty("message").GetString().Should().Contain("Save");
    }

    private async Task SeedAgentAsync(string key)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();

        var configJson = JsonSerializer.Serialize(new
        {
            type = "agent",
            provider = "anthropic",
            model = "claude-sonnet-4-6",
            systemPrompt = "You write things.",
        });

        db.Agents.Add(new AgentConfigEntity
        {
            Key = key,
            Version = 1,
            ConfigJson = configJson,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = "haa10-test",
            IsActive = true,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedWorkflowAsync(string key, string agentKey, int agentVersion)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWorkflowRepository>();

        var startNodeId = Guid.NewGuid();
        var agentNodeId = Guid.NewGuid();

        var draft = new WorkflowDraft(
            Key: key,
            Name: "HAA-10 test flow",
            MaxRoundsPerRound: 3,
            Nodes: new[]
            {
                new WorkflowNodeDraft(
                    Id: startNodeId,
                    Kind: WorkflowNodeKind.Start,
                    AgentKey: null,
                    AgentVersion: null,
                    OutputScript: null,
                    OutputPorts: new[] { "Default" },
                    LayoutX: 0,
                    LayoutY: 0),
                new WorkflowNodeDraft(
                    Id: agentNodeId,
                    Kind: WorkflowNodeKind.Agent,
                    AgentKey: agentKey,
                    AgentVersion: agentVersion,
                    OutputScript: null,
                    OutputPorts: new[] { "Done" },
                    LayoutX: 200,
                    LayoutY: 0),
            },
            Edges: new[]
            {
                new WorkflowEdgeDraft(startNodeId, "Default", agentNodeId, "in", false, 0),
            },
            Inputs: Array.Empty<WorkflowInputDraft>());

        await repo.CreateNewVersionAsync(draft);
    }

    /// <summary>
    /// Build a minimal package by hand — used for the unresolvable case where we want to feed in
    /// a workflow that references an agent we deliberately omit from the package's agents[]
    /// array. The resolver path can't help here because the seeded data wouldn't reflect the
    /// missing-reference scenario.
    /// </summary>
    private static WorkflowPackage BuildPackage(
        string entryKey,
        int entryVersion,
        string agentKeyForFirstNode,
        int agentVersion,
        bool includeAgentInPackage)
    {
        var startId = Guid.NewGuid();
        var agentNodeId = Guid.NewGuid();

        var workflow = new WorkflowPackageWorkflow(
            Key: entryKey,
            Version: entryVersion,
            Name: "Lonely",
            MaxRoundsPerRound: 3,
            Category: WorkflowCategory.Workflow,
            Tags: Array.Empty<string>(),
            CreatedAtUtc: DateTime.UtcNow,
            Nodes: new[]
            {
                new WorkflowPackageWorkflowNode(
                    Id: startId,
                    Kind: WorkflowNodeKind.Start,
                    AgentKey: null,
                    AgentVersion: null,
                    OutputScript: null,
                    OutputPorts: new[] { "Completed" },
                    LayoutX: 0,
                    LayoutY: 0),
                new WorkflowPackageWorkflowNode(
                    Id: agentNodeId,
                    Kind: WorkflowNodeKind.Agent,
                    AgentKey: agentKeyForFirstNode,
                    AgentVersion: agentVersion,
                    OutputScript: null,
                    OutputPorts: new[] { "Completed" },
                    LayoutX: 200,
                    LayoutY: 0),
            },
            Edges: new[]
            {
                new WorkflowPackageWorkflowEdge(
                    FromNodeId: startId,
                    FromPort: "Completed",
                    ToNodeId: agentNodeId,
                    ToPort: WorkflowEdge.DefaultInputPort,
                    RotatesRound: false,
                    SortOrder: 0),
            },
            Inputs: Array.Empty<WorkflowPackageWorkflowInput>());

        var agents = includeAgentInPackage
            ? new[]
            {
                new WorkflowPackageAgent(
                    Key: agentKeyForFirstNode,
                    Version: agentVersion,
                    Kind: AgentKind.Agent,
                    Config: JsonNode.Parse("""{"provider":"anthropic","model":"claude-sonnet-4-6","systemPrompt":"You write things."}"""),
                    CreatedAtUtc: DateTime.UtcNow,
                    CreatedBy: "haa10-test",
                    Outputs: Array.Empty<WorkflowPackageAgentOutput>()),
            }
            : Array.Empty<WorkflowPackageAgent>();

        return new WorkflowPackage(
            SchemaVersion: WorkflowPackageDefaults.SchemaVersion,
            Metadata: new WorkflowPackageMetadata("haa10-test", DateTime.UtcNow),
            EntryPoint: new WorkflowPackageReference(entryKey, entryVersion),
            Workflows: new[] { workflow },
            Agents: agents,
            AgentRoleAssignments: Array.Empty<WorkflowPackageAgentRoleAssignment>(),
            Roles: Array.Empty<WorkflowPackageRole>(),
            Skills: Array.Empty<WorkflowPackageSkill>(),
            McpServers: Array.Empty<WorkflowPackageMcpServer>());
    }
}
