using System.Text.Json;
using CodeFlow.Api.Assistant.Tools;
using CodeFlow.Api.Tests.Integration;
using CodeFlow.Api.WorkflowPackages;
using CodeFlow.Persistence;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CodeFlow.Api.Tests.Assistant;

/// <summary>
/// Integration tests for the read-only `get_workflow_package` companion to HAA-10. The tool
/// hands the LLM a canonical workflow-package document so it can mirror the exact shape when
/// drafting a new package — verifying the round-trip with `save_workflow_package` and the
/// 4 KB truncation contract are the load-bearing assertions.
/// </summary>
[Trait("Category", "EndToEnd")]
public sealed class GetWorkflowPackageToolTests : IClassFixture<CodeFlowApiFactory>, IAsyncLifetime
{
    private readonly CodeFlowApiFactory factory;

    public GetWorkflowPackageToolTests(CodeFlowApiFactory factory)
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
    public async Task Invoke_WithoutKey_ReturnsError()
    {
        var tool = ResolveTool();

        var result = await tool.InvokeAsync(JsonDocument.Parse("{}").RootElement, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.ResultJson.Should().Contain("key");
    }

    [Fact]
    public async Task Invoke_UnknownWorkflow_ReturnsError()
    {
        var tool = ResolveTool();
        var args = JsonSerializer.SerializeToElement(new { key = "does-not-exist" });

        var result = await tool.InvokeAsync(args, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.ResultJson.Should().Contain("does-not-exist");
    }

    [Fact]
    public async Task Invoke_HappyPath_ReturnsCanonicalPackageThatRoundTripsThroughSave()
    {
        const string agentKey = "gwp-writer";
        const string workflowKey = "gwp-flow";
        await SeedAgentAsync(agentKey);
        await SeedWorkflowAsync(workflowKey, agentKey: agentKey, agentVersion: 1);

        var getTool = ResolveTool();
        var args = JsonSerializer.SerializeToElement(new { key = workflowKey });

        var result = await getTool.InvokeAsync(args, CancellationToken.None);

        result.IsError.Should().BeFalse();
        var doc = JsonDocument.Parse(result.ResultJson);

        // Schema sanity: the response must look like a codeflow.workflow-package.v1 document.
        doc.RootElement.GetProperty("schemaVersion").GetString()
            .Should().Be(WorkflowPackageDefaults.SchemaVersion);
        doc.RootElement.GetProperty("entryPoint").GetProperty("key").GetString().Should().Be(workflowKey);
        doc.RootElement.GetProperty("workflows").GetArrayLength().Should().Be(1);
        doc.RootElement.GetProperty("agents").GetArrayLength().Should().Be(1);

        // String enums are the LLM-canonical form — the regression we are guarding against.
        doc.RootElement
            .GetProperty("workflows")[0]
            .GetProperty("category").ValueKind.Should().Be(JsonValueKind.String);
        doc.RootElement
            .GetProperty("workflows")[0]
            .GetProperty("nodes")[0]
            .GetProperty("kind").ValueKind.Should().Be(JsonValueKind.String);

        // The end-to-end claim: feeding this exact JSON into save_workflow_package must work.
        await using var scope = factory.Services.CreateAsyncScope();
        var saveTool = scope.ServiceProvider
            .GetRequiredService<IEnumerable<IAssistantTool>>()
            .OfType<SaveWorkflowPackageTool>()
            .Single();
        var saveArgs = JsonSerializer.SerializeToElement(new { package = doc.RootElement });
        var saveResult = await saveTool.InvokeAsync(saveArgs, CancellationToken.None);

        saveResult.IsError.Should().BeFalse(
            because: "the package emitted by get_workflow_package must satisfy save_workflow_package's deserializer");
        var saveParsed = JsonDocument.Parse(saveResult.ResultJson).RootElement;
        saveParsed.GetProperty("status").GetString().Should().Be("preview_ok");
    }

    [Fact]
    public async Task Invoke_TruncatesLongAgentSystemPrompt()
    {
        const string agentKey = "gwp-bigprompt";
        const string workflowKey = "gwp-bigflow";
        // 16 KB system prompt — far above the 4 KB tool cap, well under EF varchar limits.
        await SeedAgentAsync(agentKey, systemPrompt: new string('x', 16_000));
        await SeedWorkflowAsync(workflowKey, agentKey: agentKey, agentVersion: 1);

        var tool = ResolveTool();
        var args = JsonSerializer.SerializeToElement(new { key = workflowKey });

        var result = await tool.InvokeAsync(args, CancellationToken.None);

        result.IsError.Should().BeFalse();
        // The 32 KB dispatcher cap would reject an untruncated body; verify we stayed below it
        // and that the agent config still appears in the response (we trim, not delete).
        result.ResultJson.Length.Should().BeLessThan(AssistantToolDispatcher.MaxResultBytes);

        var doc = JsonDocument.Parse(result.ResultJson);
        var agentConfig = doc.RootElement.GetProperty("agents")[0].GetProperty("config");
        // Trimming an oversized JsonNode replaces it with a JSON string carrying the truncation
        // marker — the field is preserved so the model still sees it, but the byte budget is safe.
        agentConfig.ValueKind.Should().Be(JsonValueKind.String);
        agentConfig.GetString().Should().Contain("[truncated");
    }

    [Fact]
    public async Task Invoke_FullFlag_ReturnsUntruncatedAgentConfigForRoundTrip()
    {
        // The whole point of `full: true`: produce a package whose bodies are byte-identical to
        // what's in storage, so the LLM can re-emit it through `save_workflow_package` without
        // corrupting the agent prompt with a truncation marker.
        const string agentKey = "gwp-full-prompt";
        const string workflowKey = "gwp-full-flow";
        var systemPrompt = new string('z', 7296);
        await SeedAgentAsync(agentKey, systemPrompt: systemPrompt);
        await SeedWorkflowAsync(workflowKey, agentKey: agentKey, agentVersion: 1);

        var tool = ResolveTool();
        var args = JsonSerializer.SerializeToElement(new { key = workflowKey, full = true });

        var result = await tool.InvokeAsync(args, CancellationToken.None);

        result.IsError.Should().BeFalse();
        var doc = JsonDocument.Parse(result.ResultJson);
        var agentConfig = doc.RootElement.GetProperty("agents")[0].GetProperty("config");
        // With full=true the config stays an object; the embedded systemPrompt must round-trip
        // verbatim and carry no truncation marker.
        agentConfig.ValueKind.Should().Be(JsonValueKind.Object);
        var fullPrompt = agentConfig.GetProperty("systemPrompt").GetString()!;
        fullPrompt.Should().NotContain("[truncated");
        fullPrompt.Should().Be(systemPrompt);
    }

    private IAssistantTool ResolveTool()
    {
        var scope = factory.Services.CreateScope();
        return scope.ServiceProvider
            .GetRequiredService<IEnumerable<IAssistantTool>>()
            .OfType<GetWorkflowPackageTool>()
            .Single();
    }

    private async Task SeedAgentAsync(string key, string systemPrompt = "You write things.")
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();

        var configJson = JsonSerializer.Serialize(new
        {
            type = "agent",
            provider = "anthropic",
            model = "claude-sonnet-4-6",
            systemPrompt,
            outputs = new[] { new { kind = "Completed" } },
        });

        db.Agents.Add(new AgentConfigEntity
        {
            Key = key,
            Version = 1,
            ConfigJson = configJson,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = "gwp-test",
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
            Name: "GetWorkflowPackage test flow",
            MaxRoundsPerRound: 3,
            Nodes: new[]
            {
                new WorkflowNodeDraft(
                    Id: startNodeId,
                    Kind: WorkflowNodeKind.Start,
                    AgentKey: agentKey,
                    AgentVersion: agentVersion,
                    OutputScript: null,
                    OutputPorts: new[] { "Completed" },
                    LayoutX: 0,
                    LayoutY: 0),
                new WorkflowNodeDraft(
                    Id: agentNodeId,
                    Kind: WorkflowNodeKind.Agent,
                    AgentKey: agentKey,
                    AgentVersion: agentVersion,
                    OutputScript: null,
                    OutputPorts: new[] { "Completed" },
                    LayoutX: 200,
                    LayoutY: 0),
            },
            Edges: new[]
            {
                new WorkflowEdgeDraft(startNodeId, "Completed", agentNodeId, "in", false, 0),
            },
            Inputs: Array.Empty<WorkflowInputDraft>());

        await repo.CreateNewVersionAsync(draft);
    }
}
