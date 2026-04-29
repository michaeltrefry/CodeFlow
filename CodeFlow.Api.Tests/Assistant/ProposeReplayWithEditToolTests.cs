using System.Text.Json;
using CodeFlow.Api.Assistant.Tools;
using CodeFlow.Api.Tests.Integration;
using CodeFlow.Persistence;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CodeFlow.Api.Tests.Assistant;

/// <summary>
/// HAA-13: integration tests for `propose_replay_with_edit`. The tool itself does not mutate;
/// it validates the proposed substitutions against the saga subtree's recorded decisions and
/// returns a verdict the chat UI uses to render a confirmation chip. We cover:
/// preview_ok, trace_not_found, invalid (unknown agent key, ordinal out of range), unsupported
/// (synthetic subflow marker), and the malformed-edits paths (empty edits, missing fields).
/// </summary>
[Trait("Category", "EndToEnd")]
public sealed class ProposeReplayWithEditToolTests : IClassFixture<CodeFlowApiFactory>, IAsyncLifetime
{
    private readonly CodeFlowApiFactory factory;

    public ProposeReplayWithEditToolTests(CodeFlowApiFactory factory)
    {
        this.factory = factory;
    }

    public async Task InitializeAsync()
    {
        // Each test seeds its own saga + decisions; clear stale state so prior tests' rows don't
        // mask validation problems (e.g., an agent key that was inserted by a different test).
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();
        db.WorkflowSagaDecisions.RemoveRange(db.WorkflowSagaDecisions);
        db.WorkflowSagas.RemoveRange(db.WorkflowSagas);
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Invoke_ValidEdit_ReturnsPreviewOk_WithRecordedDecisions()
    {
        var traceId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        await SeedSagaAsync(traceId, correlationId, "haa13-flow");
        await SeedDecisionAsync(correlationId, traceId, agentKey: "writer", decision: "Completed", ordinal: 1);
        await SeedDecisionAsync(correlationId, traceId, agentKey: "reviewer", decision: "Rejected", ordinal: 2);

        var args = JsonSerializer.SerializeToElement(new
        {
            traceId = traceId.ToString(),
            edits = new[]
            {
                new { agentKey = "reviewer", ordinal = 1, decision = "Approved" }
            }
        });

        await using var scope = factory.Services.CreateAsyncScope();
        var tool = ResolveTool(scope);

        var result = await tool.InvokeAsync(args, CancellationToken.None);
        result.IsError.Should().BeFalse();

        var parsed = JsonDocument.Parse(result.ResultJson).RootElement;
        parsed.GetProperty("status").GetString().Should().Be("preview_ok");
        parsed.GetProperty("traceId").GetGuid().Should().Be(traceId);
        parsed.GetProperty("workflowKey").GetString().Should().Be("haa13-flow");

        var edits = parsed.GetProperty("edits").EnumerateArray().ToArray();
        edits.Should().HaveCount(1);
        edits[0].GetProperty("agentKey").GetString().Should().Be("reviewer");
        edits[0].GetProperty("ordinal").GetInt32().Should().Be(1);
        edits[0].GetProperty("decision").GetString().Should().Be("Approved");
        edits[0].GetProperty("originalDecision").GetString().Should().Be("Rejected");

        // Recorded-decisions surface lets the LLM (and the chip view) describe what's available.
        var recorded = parsed.GetProperty("recordedDecisions").EnumerateArray()
            .Select(e => new
            {
                AgentKey = e.GetProperty("agentKey").GetString(),
                Ordinal = e.GetProperty("ordinal").GetInt32(),
                OriginalDecision = e.GetProperty("originalDecision").GetString(),
            })
            .ToArray();
        recorded.Should().Contain(r =>
            r.AgentKey == "writer" && r.Ordinal == 1 && r.OriginalDecision == "Completed");
        recorded.Should().Contain(r =>
            r.AgentKey == "reviewer" && r.Ordinal == 1 && r.OriginalDecision == "Rejected");
    }

    [Fact]
    public async Task Invoke_TraceNotFound_ReturnsTraceNotFound()
    {
        var args = JsonSerializer.SerializeToElement(new
        {
            traceId = Guid.NewGuid().ToString(),
            edits = new[] { new { agentKey = "x", ordinal = 1, decision = "y" } }
        });

        await using var scope = factory.Services.CreateAsyncScope();
        var tool = ResolveTool(scope);

        var result = await tool.InvokeAsync(args, CancellationToken.None);
        result.IsError.Should().BeFalse(
            because: "an unknown trace is a reportable verdict for the LLM, not a tool failure");

        var parsed = JsonDocument.Parse(result.ResultJson).RootElement;
        parsed.GetProperty("status").GetString().Should().Be("trace_not_found");
    }

    [Fact]
    public async Task Invoke_UnknownAgentKey_ReturnsInvalid_WithRecordedDecisions()
    {
        var traceId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        await SeedSagaAsync(traceId, correlationId, "haa13-bad-agent");
        await SeedDecisionAsync(correlationId, traceId, agentKey: "writer", decision: "Completed", ordinal: 1);

        var args = JsonSerializer.SerializeToElement(new
        {
            traceId = traceId.ToString(),
            edits = new[]
            {
                new { agentKey = "ghost-agent", ordinal = 1, decision = "Approved" }
            }
        });

        await using var scope = factory.Services.CreateAsyncScope();
        var tool = ResolveTool(scope);

        var result = await tool.InvokeAsync(args, CancellationToken.None);
        result.IsError.Should().BeFalse();

        var parsed = JsonDocument.Parse(result.ResultJson).RootElement;
        parsed.GetProperty("status").GetString().Should().Be("invalid");
        var errors = parsed.GetProperty("errors").EnumerateArray().Select(e => e.GetString()).ToArray();
        errors.Should().Contain(e => e!.Contains("ghost-agent"));

        // The recordedDecisions list must round-trip so the LLM has the data to fix the edit.
        var recorded = parsed.GetProperty("recordedDecisions").EnumerateArray().ToArray();
        recorded.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Invoke_OrdinalOutOfRange_ReturnsInvalid()
    {
        var traceId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        await SeedSagaAsync(traceId, correlationId, "haa13-ordinal");
        await SeedDecisionAsync(correlationId, traceId, agentKey: "reviewer", decision: "Approved", ordinal: 1);

        var args = JsonSerializer.SerializeToElement(new
        {
            traceId = traceId.ToString(),
            edits = new[]
            {
                // Reviewer was only invoked once — ordinal 2 has nothing to substitute.
                new { agentKey = "reviewer", ordinal = 2, decision = "Rejected" }
            }
        });

        await using var scope = factory.Services.CreateAsyncScope();
        var tool = ResolveTool(scope);

        var result = await tool.InvokeAsync(args, CancellationToken.None);
        result.IsError.Should().BeFalse();

        var parsed = JsonDocument.Parse(result.ResultJson).RootElement;
        parsed.GetProperty("status").GetString().Should().Be("invalid");
        parsed.GetProperty("errors").EnumerateArray()
            .Select(e => e.GetString())
            .Should().Contain(e => e!.Contains("out of range"));
    }

    [Fact]
    public async Task Invoke_SyntheticSubflowAgentKey_ReturnsUnsupported()
    {
        var traceId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        await SeedSagaAsync(traceId, correlationId, "haa13-synthetic");
        await SeedDecisionAsync(correlationId, traceId, agentKey: "$subflow$child-flow", decision: "Done", ordinal: 1);

        var args = JsonSerializer.SerializeToElement(new
        {
            traceId = traceId.ToString(),
            edits = new[]
            {
                new { agentKey = "$subflow$child-flow", ordinal = 1, decision = "Other" }
            }
        });

        await using var scope = factory.Services.CreateAsyncScope();
        var tool = ResolveTool(scope);

        var result = await tool.InvokeAsync(args, CancellationToken.None);
        result.IsError.Should().BeFalse();

        var parsed = JsonDocument.Parse(result.ResultJson).RootElement;
        parsed.GetProperty("status").GetString().Should().Be("unsupported");
        parsed.GetProperty("errors").EnumerateArray()
            .Select(e => e.GetString())
            .Should().Contain(e => e!.Contains("synthetic"));
    }

    [Fact]
    public async Task Invoke_EmptyEditsArray_ReturnsInvalid()
    {
        var args = JsonSerializer.SerializeToElement(new
        {
            traceId = Guid.NewGuid().ToString(),
            edits = Array.Empty<object>()
        });

        await using var scope = factory.Services.CreateAsyncScope();
        var tool = ResolveTool(scope);

        var result = await tool.InvokeAsync(args, CancellationToken.None);
        result.IsError.Should().BeFalse();

        var parsed = JsonDocument.Parse(result.ResultJson).RootElement;
        parsed.GetProperty("status").GetString().Should().Be("invalid");
        parsed.GetProperty("message").GetString().Should().Contain("at least one substitution");
    }

    [Fact]
    public async Task Invoke_EditMissingDecisionOutputAndPayload_ReturnsInvalid()
    {
        var args = JsonSerializer.SerializeToElement(new
        {
            traceId = Guid.NewGuid().ToString(),
            edits = new[]
            {
                // Required: at least one of decision / output / payload. None supplied here.
                new { agentKey = "writer", ordinal = 1 }
            }
        });

        await using var scope = factory.Services.CreateAsyncScope();
        var tool = ResolveTool(scope);

        var result = await tool.InvokeAsync(args, CancellationToken.None);
        result.IsError.Should().BeFalse();

        var parsed = JsonDocument.Parse(result.ResultJson).RootElement;
        parsed.GetProperty("status").GetString().Should().Be("invalid");
        parsed.GetProperty("message").GetString().Should().Contain("decision");
    }

    [Fact]
    public async Task Invoke_TraceIdMissing_ReturnsErrorResult()
    {
        var args = JsonSerializer.SerializeToElement(new { edits = Array.Empty<object>() });

        await using var scope = factory.Services.CreateAsyncScope();
        var tool = ResolveTool(scope);

        var result = await tool.InvokeAsync(args, CancellationToken.None);
        result.IsError.Should().BeTrue(
            because: "missing required argument is a hard tool failure, not a verdict");
    }

    private static ProposeReplayWithEditTool ResolveTool(AsyncServiceScope scope) =>
        scope.ServiceProvider
            .GetServices<IAssistantTool>()
            .OfType<ProposeReplayWithEditTool>()
            .Single();

    private async Task SeedSagaAsync(Guid traceId, Guid correlationId, string workflowKey)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();
        db.WorkflowSagas.Add(new WorkflowSagaStateEntity
        {
            CorrelationId = correlationId,
            TraceId = traceId,
            CurrentState = "Completed",
            CurrentNodeId = Guid.NewGuid(),
            CurrentAgentKey = "trace-agent",
            CurrentRoundId = Guid.NewGuid(),
            RoundCount = 1,
            AgentVersionsJson = """{"trace-agent":1}""",
            DecisionHistoryJson = "[]",
            LogicEvaluationHistoryJson = "[]",
            DecisionCount = 0,
            LogicEvaluationCount = 0,
            WorkflowKey = workflowKey,
            WorkflowVersion = 1,
            InputsJson = """{"input":"hello"}""",
            CurrentInputRef = "file:///tmp/input.bin",
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
            Version = 1
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedDecisionAsync(
        Guid correlationId,
        Guid traceId,
        string agentKey,
        string decision,
        int ordinal)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();
        db.WorkflowSagaDecisions.Add(new WorkflowSagaDecisionEntity
        {
            SagaCorrelationId = correlationId,
            Ordinal = ordinal,
            TraceId = traceId,
            AgentKey = agentKey,
            AgentVersion = 1,
            Decision = decision,
            DecisionPayloadJson = null,
            RoundId = Guid.NewGuid(),
            RecordedAtUtc = DateTime.UtcNow.AddSeconds(ordinal), // stagger so order is deterministic
            NodeId = Guid.NewGuid(),
            OutputPortName = decision,
            InputRef = null,
            OutputRef = null,
            NodeEnteredAtUtc = DateTime.UtcNow.AddSeconds(ordinal),
        });
        await db.SaveChangesAsync();
    }
}
