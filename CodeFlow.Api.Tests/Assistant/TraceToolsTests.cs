using System.Text;
using System.Text.Json;
using CodeFlow.Api.Assistant.Tools;
using CodeFlow.Api.Tests.Integration;
using CodeFlow.Persistence;
using FluentAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodeFlow.Api.Tests.Assistant;

/// <summary>
/// Integration tests for HAA-5 trace tools. Each test seeds a tiny saga (or saga + decisions /
/// token-usage records / artifacts) directly via DbContext + IArtifactStore, then invokes the
/// corresponding tool through the live DI container and asserts on the JSON result.
/// </summary>
[Trait("Category", "EndToEnd")]
public sealed class TraceToolsTests : IClassFixture<CodeFlowApiFactory>, IAsyncLifetime
{
    private readonly CodeFlowApiFactory factory;

    public TraceToolsTests(CodeFlowApiFactory factory)
    {
        this.factory = factory;
    }

    public async Task InitializeAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();
        db.WorkflowSagaDecisions.RemoveRange(db.WorkflowSagaDecisions);
        db.WorkflowSagaLogicEvaluations.RemoveRange(db.WorkflowSagaLogicEvaluations);
        db.WorkflowSagas.RemoveRange(db.WorkflowSagas);
        db.TokenUsageRecords.RemoveRange(db.TokenUsageRecords);
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly JsonElement EmptyArgs = JsonDocument.Parse("{}").RootElement;

    private static JsonElement Args(object obj) =>
        JsonSerializer.SerializeToElement(obj);

    [Fact]
    public async Task ListTraces_FiltersByWorkflowKeyAndState_AndSinceUtc()
    {
        var oldTrace = await SeedSagaAsync("alpha-flow", state: "Completed", updatedAtUtc: DateTime.UtcNow.AddDays(-2));
        var recentRunning = await SeedSagaAsync("alpha-flow", state: "Running", updatedAtUtc: DateTime.UtcNow);
        var otherWorkflow = await SeedSagaAsync("beta-flow", state: "Running", updatedAtUtc: DateTime.UtcNow);

        await using var scope = factory.Services.CreateAsyncScope();
        var tool = ResolveTool<ListTracesTool>(scope);

        var all = ParseObject(await tool.InvokeAsync(EmptyArgs, CancellationToken.None));
        all.GetProperty("count").GetInt32().Should().Be(3);

        var alphaOnly = ParseObject(await tool.InvokeAsync(Args(new { workflowKey = "alpha-flow" }), CancellationToken.None));
        alphaOnly.GetProperty("count").GetInt32().Should().Be(2);

        var runningAlpha = ParseObject(await tool.InvokeAsync(Args(new { workflowKey = "alpha-flow", state = "Running" }), CancellationToken.None));
        runningAlpha.GetProperty("count").GetInt32().Should().Be(1);
        runningAlpha.GetProperty("traces")[0].GetProperty("traceId").GetGuid().Should().Be(recentRunning);

        var sinceYesterday = ParseObject(await tool.InvokeAsync(
            Args(new { sinceUtc = DateTime.UtcNow.AddDays(-1).ToString("o") }),
            CancellationToken.None));
        sinceYesterday.GetProperty("count").GetInt32().Should().Be(2); // recentRunning + otherWorkflow
    }

    [Fact]
    public async Task ListTraces_BadSinceUtc_ReturnsErrorResult()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var tool = ResolveTool<ListTracesTool>(scope);

        var result = await tool.InvokeAsync(Args(new { sinceUtc = "not-a-date" }), CancellationToken.None);
        result.IsError.Should().BeTrue();
        result.ResultJson.Should().Contain("Could not parse sinceUtc");
    }

    [Fact]
    public async Task GetTrace_ReturnsHeaderAndDecisions()
    {
        var traceId = await SeedSagaAsync("flow-with-decisions", state: "Completed");
        var nodeA = Guid.NewGuid();
        var nodeB = Guid.NewGuid();
        await SeedDecisionAsync(traceId, ordinal: 0, nodeId: nodeA, agent: "agent-a", decision: "Completed", port: "Done");
        await SeedDecisionAsync(traceId, ordinal: 1, nodeId: nodeB, agent: "agent-b", decision: "Approved", port: "Approved");

        await using var scope = factory.Services.CreateAsyncScope();
        var tool = ResolveTool<GetTraceTool>(scope);

        var result = ParseObject(await tool.InvokeAsync(Args(new { traceId }), CancellationToken.None));
        result.GetProperty("traceId").GetGuid().Should().Be(traceId);
        result.GetProperty("workflowKey").GetString().Should().Be("flow-with-decisions");
        result.GetProperty("currentState").GetString().Should().Be("Completed");

        var decisions = result.GetProperty("decisions");
        decisions.GetArrayLength().Should().Be(2);
        decisions[0].GetProperty("ordinal").GetInt32().Should().Be(0);
        decisions[0].GetProperty("agentKey").GetString().Should().Be("agent-a");
        decisions[1].GetProperty("agentKey").GetString().Should().Be("agent-b");
    }

    [Fact]
    public async Task GetTrace_NotFound_ReturnsErrorResult()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var tool = ResolveTool<GetTraceTool>(scope);

        var result = await tool.InvokeAsync(Args(new { traceId = Guid.NewGuid() }), CancellationToken.None);
        result.IsError.Should().BeTrue();
        result.ResultJson.Should().Contain("not found");
    }

    [Fact]
    public async Task GetTrace_BadGuid_ReturnsErrorResult()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var tool = ResolveTool<GetTraceTool>(scope);

        var result = await tool.InvokeAsync(Args(new { traceId = "not-a-guid" }), CancellationToken.None);
        result.IsError.Should().BeTrue();
        result.ResultJson.Should().Contain("not a valid GUID");
    }

    [Fact]
    public async Task GetTraceTimeline_ReturnsEntriesSortedByRecordedAt()
    {
        var traceId = await SeedSagaAsync("timeline-flow");
        var node = Guid.NewGuid();
        var t0 = DateTime.UtcNow.AddSeconds(-30);
        var t1 = DateTime.UtcNow.AddSeconds(-20);
        var t2 = DateTime.UtcNow.AddSeconds(-10);

        await SeedDecisionAsync(traceId, ordinal: 0, nodeId: node, agent: "a1", decision: "Completed",
            nodeEnteredAtUtc: t0, recordedAtUtc: t1);
        await SeedDecisionAsync(traceId, ordinal: 1, nodeId: node, agent: "a1", decision: "Approved",
            nodeEnteredAtUtc: t1, recordedAtUtc: t2);

        await using var scope = factory.Services.CreateAsyncScope();
        var tool = ResolveTool<GetTraceTimelineTool>(scope);

        var result = ParseObject(await tool.InvokeAsync(Args(new { traceId }), CancellationToken.None));
        result.GetProperty("count").GetInt32().Should().Be(2);
        var entries = result.GetProperty("entries");
        entries[0].GetProperty("kind").GetString().Should().Be("decision");
        entries[0].GetProperty("durationMs").GetDouble().Should().BeApproximately(10_000, precision: 50);
        entries[1].GetProperty("portOrDecision").GetString().Should().Be("Approved");
    }

    [Fact]
    public async Task GetTraceTokenUsage_AggregatesAcrossNodes()
    {
        var traceId = await SeedSagaAsync("token-flow");
        var node = Guid.NewGuid();
        var inv = Guid.NewGuid();
        await SeedTokenUsageAsync(traceId, node, inv, "anthropic", "claude-sonnet-4",
            new { input_tokens = 10, output_tokens = 20 });
        await SeedTokenUsageAsync(traceId, node, inv, "anthropic", "claude-sonnet-4",
            new { input_tokens = 5, output_tokens = 8 });

        await using var scope = factory.Services.CreateAsyncScope();
        var tool = ResolveTool<GetTraceTokenUsageTool>(scope);

        var result = ParseObject(await tool.InvokeAsync(Args(new { traceId }), CancellationToken.None));
        result.GetProperty("recordCount").GetInt32().Should().Be(2);
        var totals = result.GetProperty("total").GetProperty("totals");
        totals.GetProperty("input_tokens").GetInt64().Should().Be(15);
        totals.GetProperty("output_tokens").GetInt64().Should().Be(28);

        result.GetProperty("byNode").GetArrayLength().Should().Be(1);
        result.GetProperty("byInvocation").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task GetTraceTokenUsage_NoRecords_ReturnsEmptyRollup_NotError()
    {
        var traceId = await SeedSagaAsync("empty-flow");
        await using var scope = factory.Services.CreateAsyncScope();
        var tool = ResolveTool<GetTraceTokenUsageTool>(scope);

        var asResult = await tool.InvokeAsync(Args(new { traceId }), CancellationToken.None);
        asResult.IsError.Should().BeFalse();
        var result = ParseObject(asResult);
        result.GetProperty("recordCount").GetInt32().Should().Be(0);
        result.GetProperty("total").GetProperty("callCount").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task GetNodeIo_ReturnsTruncatedInputAndOutput()
    {
        var traceId = await SeedSagaAsync("io-flow");
        var nodeId = Guid.NewGuid();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var artifactStore = scope.ServiceProvider.GetRequiredService<IArtifactStore>();
            var input = "hello-input";
            var output = new string('x', 12_000); // exceeds 8 KB cap

            var inputUri = await artifactStore.WriteAsync(
                new MemoryStream(Encoding.UTF8.GetBytes(input)),
                new ArtifactMetadata(traceId, NewId.NextSequentialGuid(), Guid.NewGuid(), "input.txt", "text/plain", "sha256-input", input.Length),
                CancellationToken.None);

            var outputUri = await artifactStore.WriteAsync(
                new MemoryStream(Encoding.UTF8.GetBytes(output)),
                new ArtifactMetadata(traceId, NewId.NextSequentialGuid(), Guid.NewGuid(), "output.txt", "text/plain", "sha256-output", output.Length),
                CancellationToken.None);

            await SeedDecisionAsync(traceId, ordinal: 0, nodeId: nodeId, agent: "a", decision: "Completed",
                inputRef: inputUri.ToString(), outputRef: outputUri.ToString());
        }

        await using var queryScope = factory.Services.CreateAsyncScope();
        var tool = ResolveTool<GetNodeIoTool>(queryScope);

        var result = ParseObject(await tool.InvokeAsync(
            Args(new { traceId, nodeId }),
            CancellationToken.None));

        result.GetProperty("input").GetProperty("content").GetString().Should().Be("hello-input");
        result.GetProperty("input").GetProperty("contentLength").GetInt32().Should().Be(11);

        var outputBlock = result.GetProperty("output");
        outputBlock.GetProperty("contentLength").GetInt32().Should().Be(12_000);
        outputBlock.GetProperty("content").GetString().Should().Contain("[truncated");
    }

    [Fact]
    public async Task GetNodeIo_NoDecision_ReturnsErrorResult()
    {
        var traceId = await SeedSagaAsync("io-empty");
        await using var scope = factory.Services.CreateAsyncScope();
        var tool = ResolveTool<GetNodeIoTool>(scope);

        var result = await tool.InvokeAsync(
            Args(new { traceId, nodeId = Guid.NewGuid() }),
            CancellationToken.None);
        result.IsError.Should().BeTrue();
        result.ResultJson.Should().Contain("No decision row found");
    }

    private async Task<Guid> SeedSagaAsync(
        string workflowKey,
        string state = "Running",
        DateTime? updatedAtUtc = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();

        var traceId = Guid.NewGuid();
        var saga = new WorkflowSagaStateEntity
        {
            CorrelationId = Guid.NewGuid(),
            TraceId = traceId,
            CurrentState = state,
            CurrentNodeId = Guid.Empty,
            CurrentRoundId = Guid.NewGuid(),
            CurrentRoundEnteredAtUtc = DateTime.UtcNow,
            RoundCount = 1,
            WorkflowKey = workflowKey,
            WorkflowVersion = 1,
            CreatedAtUtc = updatedAtUtc ?? DateTime.UtcNow,
            UpdatedAtUtc = updatedAtUtc ?? DateTime.UtcNow,
            Version = 0,
        };
        db.WorkflowSagas.Add(saga);
        await db.SaveChangesAsync();
        return traceId;
    }

    private async Task SeedDecisionAsync(
        Guid traceId,
        int ordinal,
        Guid nodeId,
        string agent,
        string decision,
        string? port = null,
        string? inputRef = null,
        string? outputRef = null,
        DateTime? nodeEnteredAtUtc = null,
        DateTime? recordedAtUtc = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();
        var saga = await db.WorkflowSagas.SingleAsync(s => s.TraceId == traceId);

        db.WorkflowSagaDecisions.Add(new WorkflowSagaDecisionEntity
        {
            SagaCorrelationId = saga.CorrelationId,
            TraceId = traceId,
            Ordinal = ordinal,
            NodeId = nodeId,
            AgentKey = agent,
            AgentVersion = 1,
            Decision = decision,
            OutputPortName = port ?? decision,
            RoundId = Guid.NewGuid(),
            RecordedAtUtc = recordedAtUtc ?? DateTime.UtcNow,
            NodeEnteredAtUtc = nodeEnteredAtUtc,
            InputRef = inputRef,
            OutputRef = outputRef,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedTokenUsageAsync(
        Guid traceId,
        Guid nodeId,
        Guid invocationId,
        string provider,
        string model,
        object usage)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITokenUsageRecordRepository>();
        await repo.AddAsync(new TokenUsageRecord(
            Id: Guid.NewGuid(),
            TraceId: traceId,
            NodeId: nodeId,
            InvocationId: invocationId,
            ScopeChain: Array.Empty<Guid>(),
            Provider: provider,
            Model: model,
            RecordedAtUtc: DateTime.UtcNow,
            Usage: JsonSerializer.SerializeToElement(usage)));
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
