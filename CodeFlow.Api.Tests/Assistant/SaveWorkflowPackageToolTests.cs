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
    public async Task Invoke_WithUnresolvablePackage_ReturnsInvalidStatus()
    {
        // A self-contained package would include the agent in `agents[]`. We deliberately omit
        // it so the importer's resolution step throws and we surface the missing reference.
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
            because: "an unresolvable package is a reportable verdict for the LLM, not a tool failure");

        var parsed = JsonDocument.Parse(result.ResultJson).RootElement;
        parsed.GetProperty("status").GetString().Should().Be("invalid");
        // The importer's PreviewAsync surfaces self-containment failures via the exception
        // message (the MissingReferences list is populated only by the resolver / export path).
        // The message must point the LLM at the offending key so it can re-emit a fixed package.
        parsed.GetProperty("message").GetString()
            .Should().Contain("missing-agent",
                because: "the LLM must see which agent is missing to fix the package");
        parsed.GetProperty("hint").GetString()
            .Should().Contain("include every referenced entity",
                because: "the assistant should be reminded of the self-containment rule");
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
