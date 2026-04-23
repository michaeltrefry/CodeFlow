using CodeFlow.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;

namespace CodeFlow.Api.Tests.Integration;

[Trait("Category", "EndToEnd")]
public sealed class TracesEndpointsTests : IClassFixture<CodeFlowApiFactory>
{
    private readonly CodeFlowApiFactory factory;

    public TracesEndpointsTests(CodeFlowApiFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task PostTerminate_ShouldMarkRunningTraceFailedAndCancelPendingHitl()
    {
        var traceId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var roundId = Guid.NewGuid();

        await SeedTraceAsync(
            traceId,
            correlationId,
            currentState: "Running",
            includePendingHitl: true);

        using var client = factory.CreateClient();
        var response = await client.PostAsync($"/api/traces/{traceId}/terminate", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();
        var saga = await db.WorkflowSagas.SingleAsync(s => s.TraceId == traceId);
        saga.CurrentState.Should().Be("Failed");
        saga.FailureReason.Should().Be("Terminated by user.");

        var hitlTask = await db.HitlTasks.SingleAsync(task => task.TraceId == traceId);
        hitlTask.State.Should().Be(HitlTaskState.Cancelled);
        hitlTask.DecidedAtUtc.Should().NotBeNull();

        var detail = await client.GetFromJsonAsync<TraceDetailPayload>($"/api/traces/{traceId}");
        detail.Should().NotBeNull();
        detail!.CurrentState.Should().Be("Failed");
        detail.PendingHitl.Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_ShouldRemoveTerminalTraceAndHistory()
    {
        var traceId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var roundId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();

        await SeedTraceAsync(
            traceId,
            correlationId,
            currentState: "Completed",
            includePendingHitl: false);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();
            db.WorkflowSagaDecisions.Add(new WorkflowSagaDecisionEntity
            {
                SagaCorrelationId = correlationId,
                Ordinal = 0,
                TraceId = traceId,
                AgentKey = "writer",
                AgentVersion = 1,
                Decision = CodeFlow.Runtime.AgentDecisionKind.Completed,
                RoundId = roundId,
                RecordedAtUtc = DateTime.UtcNow,
                NodeId = nodeId,
                OutputPortName = "Completed",
                InputRef = "file:///tmp/input.bin",
                OutputRef = "file:///tmp/output.bin"
            });
            db.WorkflowSagaLogicEvaluations.Add(new WorkflowSagaLogicEvaluationEntity
            {
                SagaCorrelationId = correlationId,
                Ordinal = 0,
                TraceId = traceId,
                NodeId = nodeId,
                OutputPortName = "Completed",
                RoundId = roundId,
                DurationTicks = TimeSpan.FromSeconds(1).Ticks,
                LogsJson = "[]",
                RecordedAtUtc = DateTime.UtcNow
            });
            db.HitlTasks.Add(new HitlTaskEntity
            {
                TraceId = traceId,
                RoundId = roundId,
                NodeId = nodeId,
                AgentKey = "human-review",
                AgentVersion = 1,
                WorkflowKey = "cleanup-flow",
                WorkflowVersion = 1,
                InputRef = "file:///tmp/hitl-input.bin",
                State = HitlTaskState.Decided,
                CreatedAtUtc = DateTime.UtcNow,
                DecidedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        var response = await client.DeleteAsync($"/api/traces/{traceId}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();
        (await verifyDb.WorkflowSagas.AnyAsync(s => s.TraceId == traceId)).Should().BeFalse();
        (await verifyDb.WorkflowSagaDecisions.AnyAsync(d => d.TraceId == traceId)).Should().BeFalse();
        (await verifyDb.WorkflowSagaLogicEvaluations.AnyAsync(e => e.TraceId == traceId)).Should().BeFalse();
        (await verifyDb.HitlTasks.AnyAsync(task => task.TraceId == traceId)).Should().BeFalse();
    }

    [Fact]
    public async Task Delete_ShouldRejectRunningTrace()
    {
        var traceId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();

        await SeedTraceAsync(
            traceId,
            correlationId,
            currentState: "Running",
            includePendingHitl: false);

        using var client = factory.CreateClient();
        var response = await client.DeleteAsync($"/api/traces/{traceId}");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task BulkDelete_ShouldRemoveOnlyMatchingTerminalTraces()
    {
        var oldCompletedTraceId = Guid.NewGuid();
        var recentCompletedTraceId = Guid.NewGuid();
        var oldFailedTraceId = Guid.NewGuid();

        await SeedTraceAsync(
            oldCompletedTraceId,
            Guid.NewGuid(),
            currentState: "Completed",
            includePendingHitl: false,
            updatedAtUtc: DateTime.UtcNow.AddDays(-10));

        await SeedTraceAsync(
            recentCompletedTraceId,
            Guid.NewGuid(),
            currentState: "Completed",
            includePendingHitl: false,
            updatedAtUtc: DateTime.UtcNow.AddDays(-2));

        await SeedTraceAsync(
            oldFailedTraceId,
            Guid.NewGuid(),
            currentState: "Failed",
            includePendingHitl: false,
            updatedAtUtc: DateTime.UtcNow.AddDays(-10));

        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/traces/bulk-delete", new
        {
            state = "Completed",
            olderThanDays = 7
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<BulkDeleteResponsePayload>();
        payload.Should().NotBeNull();
        payload!.DeletedCount.Should().Be(1);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();
        (await db.WorkflowSagas.AnyAsync(s => s.TraceId == oldCompletedTraceId)).Should().BeFalse();
        (await db.WorkflowSagas.AnyAsync(s => s.TraceId == recentCompletedTraceId)).Should().BeTrue();
        (await db.WorkflowSagas.AnyAsync(s => s.TraceId == oldFailedTraceId)).Should().BeTrue();
    }

    private async Task SeedTraceAsync(
        Guid traceId,
        Guid correlationId,
        string currentState,
        bool includePendingHitl,
        DateTime? updatedAtUtc = null)
    {
        var roundId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var timestamp = updatedAtUtc ?? DateTime.UtcNow;

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();

        db.WorkflowSagas.Add(new WorkflowSagaStateEntity
        {
            CorrelationId = correlationId,
            TraceId = traceId,
            CurrentState = currentState,
            CurrentNodeId = nodeId,
            CurrentAgentKey = "trace-agent",
            CurrentRoundId = roundId,
            RoundCount = 1,
            AgentVersionsJson = """{"trace-agent":1}""",
            DecisionHistoryJson = "[]",
            LogicEvaluationHistoryJson = "[]",
            DecisionCount = 0,
            LogicEvaluationCount = 0,
            WorkflowKey = "cleanup-flow",
            WorkflowVersion = 1,
            InputsJson = """{"input":"hello"}""",
            CurrentInputRef = "file:///tmp/input.bin",
            CreatedAtUtc = timestamp,
            UpdatedAtUtc = timestamp,
            Version = 1
        });

        if (includePendingHitl)
        {
            db.HitlTasks.Add(new HitlTaskEntity
            {
                TraceId = traceId,
                RoundId = roundId,
                NodeId = nodeId,
                AgentKey = "human-review",
                AgentVersion = 1,
                WorkflowKey = "cleanup-flow",
                WorkflowVersion = 1,
                InputRef = "file:///tmp/pending-hitl.bin",
                InputPreview = "Need review",
                State = HitlTaskState.Pending,
                CreatedAtUtc = DateTime.UtcNow
            });
        }

        await db.SaveChangesAsync();
    }

    private sealed record TraceDetailPayload(
        Guid TraceId,
        string CurrentState,
        IReadOnlyList<HitlTaskPayload> PendingHitl);

    private sealed record BulkDeleteResponsePayload(int DeletedCount);

    private sealed record HitlTaskPayload(long Id);
}
