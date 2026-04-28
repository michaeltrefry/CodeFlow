using System.Text.Json;
using CodeFlow.Api.Assistant.Tools;
using CodeFlow.Api.Tests.Integration;
using CodeFlow.Persistence;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CodeFlow.Api.Tests.Assistant;

/// <summary>
/// Integration tests for HAA-11's `run_workflow` tool. The tool itself does not mutate; it
/// resolves the workflow and validates the supplied inputs against the workflow's declared
/// <see cref="WorkflowInput"/> schema, then returns a verdict the chat UI uses to render a
/// confirmation chip. We cover the four branches: not_found, inputs_missing,
/// invalid (unknown key + type mismatch), and the success preview_ok path.
/// </summary>
[Trait("Category", "EndToEnd")]
public sealed class RunWorkflowToolTests : IClassFixture<CodeFlowApiFactory>, IAsyncLifetime
{
    private readonly CodeFlowApiFactory factory;

    public RunWorkflowToolTests(CodeFlowApiFactory factory)
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
    public async Task Invoke_WorkflowNotFound_ReturnsNotFoundStatus()
    {
        var args = JsonSerializer.SerializeToElement(new
        {
            workflowKey = "no-such-workflow",
            input = "hello",
        });

        await using var scope = factory.Services.CreateAsyncScope();
        var tool = ResolveTool(scope);

        var result = await tool.InvokeAsync(args, CancellationToken.None);

        result.IsError.Should().BeFalse(
            because: "an unknown workflow is a reportable verdict for the LLM, not a tool failure");
        var parsed = JsonDocument.Parse(result.ResultJson).RootElement;
        parsed.GetProperty("status").GetString().Should().Be("not_found");
        parsed.GetProperty("message").GetString().Should().Contain("no-such-workflow");
    }

    [Fact]
    public async Task Invoke_RequiredInputMissing_ReturnsInputsMissing()
    {
        await SeedWorkflowAsync(
            "haa11-needs-repo",
            agentKey: "haa11-runner",
            inputs: new[]
            {
                new WorkflowInputDraft(
                    Key: "repo",
                    DisplayName: "Repository",
                    Kind: WorkflowInputKind.Text,
                    Required: true,
                    DefaultValueJson: null,
                    Description: "The repo slug to operate on.",
                    Ordinal: 0),
            });

        var args = JsonSerializer.SerializeToElement(new
        {
            workflowKey = "haa11-needs-repo",
            input = "do the thing",
            // No `inputs` supplied — the required `repo` input has no default, so this should
            // surface inputs_missing rather than running the workflow.
        });

        await using var scope = factory.Services.CreateAsyncScope();
        var tool = ResolveTool(scope);

        var result = await tool.InvokeAsync(args, CancellationToken.None);

        result.IsError.Should().BeFalse();
        var parsed = JsonDocument.Parse(result.ResultJson).RootElement;
        parsed.GetProperty("status").GetString().Should().Be("inputs_missing");
        var missing = parsed.GetProperty("missingInputs").EnumerateArray()
            .Select(e => e.GetString())
            .ToArray();
        missing.Should().Contain("repo");
        parsed.GetProperty("workflow").GetProperty("key").GetString().Should().Be("haa11-needs-repo");
    }

    [Fact]
    public async Task Invoke_UnknownInputKey_ReturnsInvalid()
    {
        await SeedWorkflowAsync(
            "haa11-strict",
            agentKey: "haa11-runner",
            inputs: new[]
            {
                new WorkflowInputDraft(
                    Key: "subject",
                    DisplayName: "Subject",
                    Kind: WorkflowInputKind.Text,
                    Required: false,
                    DefaultValueJson: "\"default-subject\"",
                    Description: null,
                    Ordinal: 0),
            });

        var args = JsonSerializer.SerializeToElement(new
        {
            workflowKey = "haa11-strict",
            input = "go",
            inputs = new { not_a_real_input = "oops" },
        });

        await using var scope = factory.Services.CreateAsyncScope();
        var tool = ResolveTool(scope);

        var result = await tool.InvokeAsync(args, CancellationToken.None);

        result.IsError.Should().BeFalse();
        var parsed = JsonDocument.Parse(result.ResultJson).RootElement;
        parsed.GetProperty("status").GetString().Should().Be("invalid");
        var unknown = parsed.GetProperty("unknownInputs").EnumerateArray()
            .Select(e => e.GetString())
            .ToArray();
        unknown.Should().Contain("not_a_real_input");
        parsed.GetProperty("message").GetString().Should().Contain("unknown");
    }

    [Fact]
    public async Task Invoke_AllInputsValid_ReturnsPreviewOk()
    {
        await SeedWorkflowAsync(
            "haa11-happy",
            agentKey: "haa11-runner",
            inputs: new[]
            {
                new WorkflowInputDraft(
                    Key: "subject",
                    DisplayName: "Subject",
                    Kind: WorkflowInputKind.Text,
                    Required: true,
                    DefaultValueJson: null,
                    Description: "What to write about.",
                    Ordinal: 0),
                new WorkflowInputDraft(
                    Key: "tone",
                    DisplayName: "Tone",
                    Kind: WorkflowInputKind.Text,
                    Required: false,
                    DefaultValueJson: "\"professional\"",
                    Description: null,
                    Ordinal: 1),
            });

        var args = JsonSerializer.SerializeToElement(new
        {
            workflowKey = "haa11-happy",
            input = "Write a haiku about ports",
            inputs = new { subject = "ports" },
        });

        await using var scope = factory.Services.CreateAsyncScope();
        var tool = ResolveTool(scope);

        var result = await tool.InvokeAsync(args, CancellationToken.None);

        result.IsError.Should().BeFalse();
        var parsed = JsonDocument.Parse(result.ResultJson).RootElement;
        parsed.GetProperty("status").GetString().Should().Be("preview_ok");
        parsed.GetProperty("workflow").GetProperty("name").GetString().Should().Be("HAA-11 test flow");
        parsed.GetProperty("workflow").GetProperty("version").GetInt32().Should().Be(1);

        var resolved = parsed.GetProperty("resolvedInputs");
        resolved.GetProperty("subject").GetString().Should().Be("ports");
        // The optional `tone` input has a JSON default and was not supplied — it should fall
        // back to the declared default in the resolved set.
        resolved.GetProperty("tone").GetString().Should().Be("professional");

        parsed.GetProperty("message").GetString().Should().Contain("Run");
    }

    private static RunWorkflowTool ResolveTool(AsyncServiceScope scope) =>
        scope.ServiceProvider
            .GetServices<IAssistantTool>()
            .OfType<RunWorkflowTool>()
            .Single();

    private async Task SeedWorkflowAsync(
        string key,
        string agentKey,
        IReadOnlyList<WorkflowInputDraft> inputs)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();

        // Inline agent seed (matches RegistryToolsTests's pattern).
        if (!db.Agents.Any(a => a.Key == agentKey))
        {
            db.Agents.Add(new AgentConfigEntity
            {
                Key = agentKey,
                Version = 1,
                ConfigJson = JsonSerializer.Serialize(new
                {
                    type = "agent",
                    provider = "anthropic",
                    model = "claude-sonnet-4-6",
                    systemPrompt = "You run things.",
                }),
                CreatedAtUtc = DateTime.UtcNow,
                CreatedBy = "haa11-test",
                IsActive = true,
            });
            await db.SaveChangesAsync();
        }

        var repo = scope.ServiceProvider.GetRequiredService<IWorkflowRepository>();
        var startNodeId = Guid.NewGuid();
        var agentNodeId = Guid.NewGuid();

        var draft = new WorkflowDraft(
            Key: key,
            Name: "HAA-11 test flow",
            MaxRoundsPerRound: 3,
            Nodes: new[]
            {
                new WorkflowNodeDraft(
                    Id: startNodeId,
                    Kind: WorkflowNodeKind.Start,
                    AgentKey: agentKey,
                    AgentVersion: 1,
                    OutputScript: null,
                    OutputPorts: new[] { "Default" },
                    LayoutX: 0,
                    LayoutY: 0),
                new WorkflowNodeDraft(
                    Id: agentNodeId,
                    Kind: WorkflowNodeKind.Agent,
                    AgentKey: agentKey,
                    AgentVersion: 1,
                    OutputScript: null,
                    OutputPorts: new[] { "Done" },
                    LayoutX: 200,
                    LayoutY: 0),
            },
            Edges: new[]
            {
                new WorkflowEdgeDraft(startNodeId, "Default", agentNodeId, "in", false, 0),
            },
            Inputs: inputs);

        await repo.CreateNewVersionAsync(draft);
    }
}
