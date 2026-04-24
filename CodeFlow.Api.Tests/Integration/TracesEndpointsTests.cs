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

    [Fact]
    public async Task SubmitHitlDecision_ShouldRenderDecisionOutputTemplateServerSide()
    {
        // An agent with a matching DecisionOutputTemplate renders server-side using FieldValues
        // and context from the saga — the client-supplied OutputText is ignored.
        var agentKey = $"hitl-render-{Guid.NewGuid():N}";
        var traceId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var roundId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();
            db.WorkflowSagas.Add(new WorkflowSagaStateEntity
            {
                CorrelationId = correlationId,
                TraceId = traceId,
                CurrentState = "Running",
                CurrentNodeId = nodeId,
                CurrentAgentKey = agentKey,
                CurrentRoundId = roundId,
                RoundCount = 1,
                AgentVersionsJson = $"{{\"{agentKey}\":1}}",
                DecisionHistoryJson = "[]",
                LogicEvaluationHistoryJson = "[]",
                DecisionCount = 0,
                LogicEvaluationCount = 0,
                WorkflowKey = "hitl-template-flow",
                WorkflowVersion = 1,
                InputsJson = """{"headline":"Ship it"}""",
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
                Version = 1
            });
            db.Agents.Add(new AgentConfigEntity
            {
                Key = agentKey,
                Version = 1,
                IsActive = true,
                ConfigJson = """
                {
                    "type": "hitl",
                    "decisionOutputTemplates": {
                        "Approved": "[{{ decision }}] {{ input.feedback }} // ctx={{ context.headline }}"
                    }
                }
                """,
                CreatedAtUtc = DateTime.UtcNow
            });
            db.HitlTasks.Add(new HitlTaskEntity
            {
                TraceId = traceId,
                RoundId = roundId,
                NodeId = nodeId,
                AgentKey = agentKey,
                AgentVersion = 1,
                WorkflowKey = "hitl-template-flow",
                WorkflowVersion = 1,
                InputRef = "file:///tmp/hitl-input.bin",
                InputPreview = "review this",
                State = HitlTaskState.Pending,
                CreatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        var body = new
        {
            decision = "Approved",
            outputPortName = "Approved",
            outputText = "client-rendered content that must be ignored",
            fieldValues = new Dictionary<string, object>
            {
                ["feedback"] = "looks good"
            }
        };

        var response = await client.PostAsJsonAsync($"/api/traces/{traceId}/hitl-decision", body);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Artifacts are stored as {root}/{traceId:N}/{roundId:N}/{artifactId:N}.bin
        var content = await ReadSoleArtifactAsync(traceId);
        content.Should().Be("[Approved] looks good // ctx=Ship it");
    }

    [Fact]
    public async Task SubmitHitlDecision_ShouldFallBackToClientOutputText_WhenNoTemplateMatches()
    {
        // An agent with no DecisionOutputTemplates keeps legacy behavior: the client-rendered
        // OutputText is written as the artifact.
        var agentKey = $"hitl-legacy-{Guid.NewGuid():N}";
        var traceId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var roundId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();
            db.WorkflowSagas.Add(new WorkflowSagaStateEntity
            {
                CorrelationId = correlationId,
                TraceId = traceId,
                CurrentState = "Running",
                CurrentNodeId = nodeId,
                CurrentAgentKey = agentKey,
                CurrentRoundId = roundId,
                RoundCount = 1,
                AgentVersionsJson = $"{{\"{agentKey}\":1}}",
                DecisionHistoryJson = "[]",
                LogicEvaluationHistoryJson = "[]",
                DecisionCount = 0,
                LogicEvaluationCount = 0,
                WorkflowKey = "hitl-legacy-flow",
                WorkflowVersion = 1,
                InputsJson = "{}",
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
                Version = 1
            });
            db.Agents.Add(new AgentConfigEntity
            {
                Key = agentKey,
                Version = 1,
                IsActive = true,
                ConfigJson = """{ "type": "hitl" }""",
                CreatedAtUtc = DateTime.UtcNow
            });
            db.HitlTasks.Add(new HitlTaskEntity
            {
                TraceId = traceId,
                RoundId = roundId,
                NodeId = nodeId,
                AgentKey = agentKey,
                AgentVersion = 1,
                WorkflowKey = "hitl-legacy-flow",
                WorkflowVersion = 1,
                InputRef = "file:///tmp/hitl-input.bin",
                InputPreview = "review this",
                State = HitlTaskState.Pending,
                CreatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        var body = new
        {
            decision = "Approved",
            outputPortName = "Approved",
            outputText = "legacy client-rendered body"
        };

        var response = await client.PostAsJsonAsync($"/api/traces/{traceId}/hitl-decision", body);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await ReadSoleArtifactAsync(traceId);
        content.Should().Be("legacy client-rendered body");
    }

    [Fact]
    public async Task SubmitHitlDecision_ShouldReturn422_WhenTemplateFailsToRender()
    {
        var agentKey = $"hitl-fail-{Guid.NewGuid():N}";
        var traceId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var roundId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();
            db.WorkflowSagas.Add(new WorkflowSagaStateEntity
            {
                CorrelationId = correlationId,
                TraceId = traceId,
                CurrentState = "Running",
                CurrentNodeId = nodeId,
                CurrentAgentKey = agentKey,
                CurrentRoundId = roundId,
                RoundCount = 1,
                AgentVersionsJson = $"{{\"{agentKey}\":1}}",
                DecisionHistoryJson = "[]",
                LogicEvaluationHistoryJson = "[]",
                DecisionCount = 0,
                LogicEvaluationCount = 0,
                WorkflowKey = "hitl-bad-template",
                WorkflowVersion = 1,
                InputsJson = "{}",
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
                Version = 1
            });
            db.Agents.Add(new AgentConfigEntity
            {
                Key = agentKey,
                Version = 1,
                IsActive = true,
                ConfigJson = """
                {
                    "type": "hitl",
                    "decisionOutputTemplates": {
                        "Approved": "{{ if unterminated"
                    }
                }
                """,
                CreatedAtUtc = DateTime.UtcNow
            });
            db.HitlTasks.Add(new HitlTaskEntity
            {
                TraceId = traceId,
                RoundId = roundId,
                NodeId = nodeId,
                AgentKey = agentKey,
                AgentVersion = 1,
                WorkflowKey = "hitl-bad-template",
                WorkflowVersion = 1,
                InputRef = "file:///tmp/hitl-input.bin",
                InputPreview = "preview",
                State = HitlTaskState.Pending,
                CreatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        var body = new
        {
            decision = "Approved",
            outputPortName = "Approved"
        };

        var response = await client.PostAsJsonAsync($"/api/traces/{traceId}/hitl-decision", body);
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        // Pending task must remain Pending — the failed render did not consume it.
        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();
        var task = await verifyDb.HitlTasks.SingleAsync(t => t.TraceId == traceId);
        task.State.Should().Be(HitlTaskState.Pending);
    }

    private async Task<string> ReadSoleArtifactAsync(Guid traceId)
    {
        // The running host canonicalizes the artifact root via Path.GetFullPath, which on macOS
        // resolves /tmp through /private/tmp — diverging from factory.ArtifactRoot. Reach into the
        // live IArtifactStore to get the effective root.
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IArtifactStore>();
        var rootField = store.GetType().GetField(
            "rootDirectory",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var root = rootField?.GetValue(store) as string
            ?? throw new InvalidOperationException("Unable to resolve artifact store root directory.");

        var traceRoot = Path.Combine(root, traceId.ToString("N"));
        Directory.Exists(traceRoot).Should().BeTrue(
            $"hitl submit should have written an artifact under '{traceRoot}'");
        var files = Directory.GetFiles(traceRoot, "*.bin", SearchOption.AllDirectories);
        files.Should().HaveCount(1);
        return await File.ReadAllTextAsync(files[0]);
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

    [Fact]
    public async Task GetTrace_ShouldAggregatePendingHitlFromSubflowDescendants()
    {
        // S7: pending HITL tasks from descendant child sagas (those with ParentTraceId pointing
        // to this or any ancestor) are surfaced on the root trace's PendingHitl, each decorated
        // with OriginTraceId and SubflowPath so the UI can label them.
        var rootTraceId = Guid.NewGuid();
        var childTraceId = Guid.NewGuid();
        var grandchildTraceId = Guid.NewGuid();
        var rootCorrelationId = Guid.NewGuid();
        var childCorrelationId = Guid.NewGuid();
        var grandchildCorrelationId = Guid.NewGuid();
        var sharedRoundId = Guid.NewGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();

            db.WorkflowSagas.AddRange(
                NewSaga(rootCorrelationId, rootTraceId, "parent-wf", parentTraceId: null, subflowDepth: 0),
                NewSaga(childCorrelationId, childTraceId, "shared-utility", parentTraceId: rootTraceId, subflowDepth: 1),
                NewSaga(grandchildCorrelationId, grandchildTraceId, "leaf-utility", parentTraceId: childTraceId, subflowDepth: 2));

            db.HitlTasks.AddRange(
                NewHitl(rootTraceId, sharedRoundId, "parent-human", "parent-wf", 1),
                NewHitl(childTraceId, sharedRoundId, "child-human", "shared-utility", 2),
                NewHitl(grandchildTraceId, sharedRoundId, "grandchild-human", "leaf-utility", 3));

            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        var detail = await client.GetFromJsonAsync<TraceDetailPayload>($"/api/traces/{rootTraceId}");
        detail.Should().NotBeNull();
        detail!.PendingHitl.Should().HaveCount(3);

        var rootHitl = detail.PendingHitl.Single(h => h.AgentKey == "parent-human");
        rootHitl.TraceId.Should().Be(rootTraceId);
        rootHitl.OriginTraceId.Should().Be(rootTraceId);
        rootHitl.SubflowPath.Should().BeEmpty("root HITL has no subflow path");

        var childHitl = detail.PendingHitl.Single(h => h.AgentKey == "child-human");
        childHitl.TraceId.Should().Be(childTraceId);
        childHitl.OriginTraceId.Should().Be(childTraceId);
        childHitl.SubflowPath.Should().Equal("shared-utility");

        var grandchildHitl = detail.PendingHitl.Single(h => h.AgentKey == "grandchild-human");
        grandchildHitl.TraceId.Should().Be(grandchildTraceId);
        grandchildHitl.OriginTraceId.Should().Be(grandchildTraceId);
        grandchildHitl.SubflowPath.Should().Equal("shared-utility", "leaf-utility");
    }

    [Fact]
    public async Task GetTrace_ShouldIncludeHitlAtDepth3()
    {
        // S7: the max legal subflow depth is 3, so a chain root→A→B→C should aggregate the
        // HITL in C onto the root.
        var rootTraceId = Guid.NewGuid();
        var aTraceId = Guid.NewGuid();
        var bTraceId = Guid.NewGuid();
        var cTraceId = Guid.NewGuid();
        var cRoundId = Guid.NewGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();
            db.WorkflowSagas.AddRange(
                NewSaga(Guid.NewGuid(), rootTraceId, "root-wf", parentTraceId: null, subflowDepth: 0),
                NewSaga(Guid.NewGuid(), aTraceId, "A", parentTraceId: rootTraceId, subflowDepth: 1),
                NewSaga(Guid.NewGuid(), bTraceId, "B", parentTraceId: aTraceId, subflowDepth: 2),
                NewSaga(Guid.NewGuid(), cTraceId, "C", parentTraceId: bTraceId, subflowDepth: 3));
            db.HitlTasks.Add(NewHitl(cTraceId, cRoundId, "c-human", "C", 1));
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        var detail = await client.GetFromJsonAsync<TraceDetailPayload>($"/api/traces/{rootTraceId}");
        detail.Should().NotBeNull();
        detail!.PendingHitl.Should().HaveCount(1);
        var hitl = detail.PendingHitl.Single();
        hitl.OriginTraceId.Should().Be(cTraceId);
        hitl.SubflowPath.Should().Equal("A", "B", "C");
    }

    private static WorkflowSagaStateEntity NewSaga(
        Guid correlationId,
        Guid traceId,
        string workflowKey,
        Guid? parentTraceId,
        int subflowDepth)
    {
        var now = DateTime.UtcNow;
        return new WorkflowSagaStateEntity
        {
            CorrelationId = correlationId,
            TraceId = traceId,
            CurrentState = "Running",
            CurrentNodeId = Guid.NewGuid(),
            CurrentAgentKey = "agent",
            CurrentRoundId = Guid.NewGuid(),
            RoundCount = 0,
            AgentVersionsJson = "{}",
            DecisionHistoryJson = "[]",
            LogicEvaluationHistoryJson = "[]",
            DecisionCount = 0,
            LogicEvaluationCount = 0,
            WorkflowKey = workflowKey,
            WorkflowVersion = 1,
            InputsJson = "{}",
            ParentTraceId = parentTraceId,
            ParentNodeId = parentTraceId is null ? null : Guid.NewGuid(),
            ParentRoundId = parentTraceId is null ? null : Guid.NewGuid(),
            SubflowDepth = subflowDepth,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Version = 1,
        };
    }

    private static HitlTaskEntity NewHitl(
        Guid traceId,
        Guid roundId,
        string agentKey,
        string workflowKey,
        long inputRefSalt)
    {
        return new HitlTaskEntity
        {
            TraceId = traceId,
            RoundId = roundId,
            NodeId = Guid.NewGuid(),
            AgentKey = agentKey,
            AgentVersion = 1,
            WorkflowKey = workflowKey,
            WorkflowVersion = 1,
            InputRef = $"file:///tmp/hitl-{inputRefSalt}.bin",
            InputPreview = "preview",
            State = HitlTaskState.Pending,
            CreatedAtUtc = DateTime.UtcNow,
        };
    }

    private sealed record TraceDetailPayload(
        Guid TraceId,
        string CurrentState,
        IReadOnlyList<HitlTaskPayload> PendingHitl);

    private sealed record BulkDeleteResponsePayload(int DeletedCount);

    private sealed record HitlTaskPayload(
        long Id,
        Guid TraceId,
        string AgentKey,
        Guid? OriginTraceId,
        IReadOnlyList<string>? SubflowPath);
}
