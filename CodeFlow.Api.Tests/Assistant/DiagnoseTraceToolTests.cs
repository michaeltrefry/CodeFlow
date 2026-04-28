using System.Text.Json;
using CodeFlow.Api.Assistant.Tools;
using CodeFlow.Api.Tests.Integration;
using CodeFlow.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodeFlow.Api.Tests.Assistant;

/// <summary>
/// Integration tests for HAA-12's `diagnose_trace` tool. Each test seeds a saga + decisions +
/// (optionally) token records via DbContext, invokes the tool through DI, and asserts on the
/// JSON verdict. The tool is read-only — it does not mutate — so we cover the four shapes the
/// chat UI cares about: failed trace, completed-but-anomalous trace, clean completed trace,
/// and the not-found error path.
/// </summary>
[Trait("Category", "EndToEnd")]
public sealed class DiagnoseTraceToolTests : IClassFixture<CodeFlowApiFactory>, IAsyncLifetime
{
    private readonly CodeFlowApiFactory factory;

    public DiagnoseTraceToolTests(CodeFlowApiFactory factory)
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

    [Fact]
    public async Task Invoke_TraceNotFound_ReturnsError()
    {
        var args = JsonSerializer.SerializeToElement(new { traceId = Guid.NewGuid().ToString() });

        await using var scope = factory.Services.CreateAsyncScope();
        var tool = ResolveTool(scope);

        var result = await tool.InvokeAsync(args, CancellationToken.None);
        result.IsError.Should().BeTrue();
        result.ResultJson.Should().Contain("not found");
    }

    [Fact]
    public async Task Invoke_FailedTrace_SurfacesFailingNodeAndReplaySuggestion()
    {
        var traceId = await SeedSagaAsync(
            "haa12-fails",
            state: "Failed",
            failureReason: "Required input 'subject' missing.");

        var failingNode = Guid.NewGuid();
        await SeedDecisionAsync(
            traceId,
            ordinal: 0,
            nodeId: failingNode,
            agent: "haa12-writer",
            decision: "Failed",
            port: "Failed");

        await using var scope = factory.Services.CreateAsyncScope();
        var tool = ResolveTool(scope);

        var result = await tool.InvokeAsync(
            JsonSerializer.SerializeToElement(new { traceId }),
            CancellationToken.None);

        result.IsError.Should().BeFalse(because: result.ResultJson);
        var parsed = JsonDocument.Parse(result.ResultJson).RootElement;

        parsed.GetProperty("currentState").GetString().Should().Be("Failed");
        parsed.GetProperty("failureReason").GetString().Should().Contain("subject");
        parsed.GetProperty("summary").GetString().Should().Contain("Failed");

        var failing = parsed.GetProperty("failingNodes");
        failing.GetArrayLength().Should().BeGreaterThan(0,
            because: "the saga ended Failed so the last decision should be flagged");

        var firstFailing = failing[0];
        firstFailing.GetProperty("agentKey").GetString().Should().Be("haa12-writer");
        firstFailing.GetProperty("deepLink").GetString().Should().Be($"/traces/{traceId}");
        firstFailing.GetProperty("agentDeepLink").GetString().Should().Be("/agents/haa12-writer");

        // Replay-with-edit suggestion should appear for any failed trace.
        var suggestions = parsed.GetProperty("suggestions").EnumerateArray()
            .Select(s => s.GetProperty("kind").GetString())
            .ToArray();
        suggestions.Should().Contain("replay_with_edit");
        suggestions.Should().Contain("review_agent");

        parsed.GetProperty("links").GetProperty("trace").GetString().Should().Be($"/traces/{traceId}");
    }

    [Fact]
    public async Task Invoke_CompletedTraceWithTokenSpike_FlagsAnomalyAndNoFailure()
    {
        var traceId = await SeedSagaAsync("haa12-spiky", state: "Completed");

        // Two normal nodes (a few hundred tokens each) and one wildly above the absolute spike
        // threshold (50_000) so the heuristic flags it.
        var normalNode1 = Guid.NewGuid();
        var normalNode2 = Guid.NewGuid();
        var spikeNode = Guid.NewGuid();

        await SeedTokenUsageAsync(traceId, normalNode1, "anthropic", "claude-sonnet-4",
            new { input_tokens = 100, output_tokens = 50 });
        await SeedTokenUsageAsync(traceId, normalNode2, "anthropic", "claude-sonnet-4",
            new { input_tokens = 200, output_tokens = 80 });
        await SeedTokenUsageAsync(traceId, spikeNode, "anthropic", "claude-sonnet-4",
            new { input_tokens = 80_000, output_tokens = 5_000 });

        await using var scope = factory.Services.CreateAsyncScope();
        var tool = ResolveTool(scope);

        var result = await tool.InvokeAsync(
            JsonSerializer.SerializeToElement(new { traceId }),
            CancellationToken.None);

        result.IsError.Should().BeFalse(because: result.ResultJson);
        var parsed = JsonDocument.Parse(result.ResultJson).RootElement;

        parsed.GetProperty("currentState").GetString().Should().Be("Completed");
        parsed.GetProperty("failingNodes").GetArrayLength().Should().Be(0);

        var anomalies = parsed.GetProperty("anomalies").EnumerateArray()
            .Where(a => a.GetProperty("kind").GetString() == "token_spike")
            .ToArray();
        anomalies.Length.Should().BeGreaterThan(0,
            because: "an 85k-token node on a trace with sub-300-token peers should be flagged");

        var spike = anomalies[0];
        spike.GetProperty("nodeId").GetGuid().Should().Be(spikeNode);
        spike.GetProperty("evidence").GetProperty("nodeTotalTokens").GetInt64().Should().BeGreaterThan(50_000);

        // Suggestions should include inspect_node_io for the spike but NOT replay_with_edit
        // because the saga didn't fail.
        var suggestionKinds = parsed.GetProperty("suggestions").EnumerateArray()
            .Select(s => s.GetProperty("kind").GetString())
            .ToArray();
        suggestionKinds.Should().Contain("inspect_node_io");
        suggestionKinds.Should().NotContain("replay_with_edit");
    }

    [Fact]
    public async Task Invoke_CleanCompletedTrace_ReturnsEmptyArrays()
    {
        var traceId = await SeedSagaAsync("haa12-clean", state: "Completed");
        await SeedDecisionAsync(traceId, ordinal: 0, nodeId: Guid.NewGuid(), agent: "haa12-writer",
            decision: "Completed", port: "Done");
        await SeedTokenUsageAsync(traceId, Guid.NewGuid(), "anthropic", "claude-sonnet-4",
            new { input_tokens = 200, output_tokens = 100 });

        await using var scope = factory.Services.CreateAsyncScope();
        var tool = ResolveTool(scope);

        var result = await tool.InvokeAsync(
            JsonSerializer.SerializeToElement(new { traceId }),
            CancellationToken.None);

        result.IsError.Should().BeFalse();
        var parsed = JsonDocument.Parse(result.ResultJson).RootElement;

        parsed.GetProperty("currentState").GetString().Should().Be("Completed");
        parsed.GetProperty("failingNodes").GetArrayLength().Should().Be(0);
        parsed.GetProperty("anomalies").GetArrayLength().Should().Be(0);
        parsed.GetProperty("suggestions").GetArrayLength().Should().Be(0);
        parsed.GetProperty("summary").GetString().Should().Contain("no failures or anomalies");
    }

    private static DiagnoseTraceTool ResolveTool(AsyncServiceScope scope) =>
        scope.ServiceProvider
            .GetServices<IAssistantTool>()
            .OfType<DiagnoseTraceTool>()
            .Single();

    private async Task<Guid> SeedSagaAsync(
        string workflowKey,
        string state = "Running",
        string? failureReason = null)
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
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
            Version = 0,
            FailureReason = failureReason,
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
        string? port = null)
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
            RecordedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedTokenUsageAsync(
        Guid traceId,
        Guid nodeId,
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
            InvocationId: Guid.NewGuid(),
            ScopeChain: Array.Empty<Guid>(),
            Provider: provider,
            Model: model,
            RecordedAtUtc: DateTime.UtcNow,
            Usage: JsonSerializer.SerializeToElement(usage)));
    }
}
