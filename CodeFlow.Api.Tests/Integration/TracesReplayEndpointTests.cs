using System.Net;
using System.Net.Http.Json;
using System.Text;
using CodeFlow.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodeFlow.Api.Tests.Integration;

[Trait("Category", "EndToEnd")]
public sealed class TracesReplayEndpointTests : IClassFixture<CodeFlowApiFactory>
{
    private readonly CodeFlowApiFactory factory;

    public TracesReplayEndpointTests(CodeFlowApiFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Replay_NonExistentTrace_Returns404()
    {
        using var client = factory.CreateClient();
        var response = await client.PostAsync(
            $"/api/traces/{Guid.NewGuid()}/replay",
            JsonContent.Create(new { }));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Replay_RoundTripIdentity_Returns200WithReplayEvents()
    {
        var (traceId, _, _, _) = await SeedSimpleEchoTraceAsync();

        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            $"/api/traces/{traceId}/replay",
            new { });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<ReplayResponsePayload>();

        payload.Should().NotBeNull();
        payload!.OriginalTraceId.Should().Be(traceId);
        payload.ReplayState.Should().Be("Completed");
        payload.ReplayTerminalPort.Should().Be("Completed");
        payload.Drift.Level.Should().Be("None");
        payload.Decisions.Should().ContainSingle();
        payload.Decisions[0].AgentKey.Should().Be("echo");
        payload.Decisions[0].OrdinalPerAgent.Should().Be(1);
        payload.ReplayEvents.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Replay_LengtheningEditWithoutAdditionalMocks_ReturnsQueueExhaustedFailure()
    {
        var (traceId, _, _, _) = await SeedSimpleEchoTraceAsync(decisionPort: "Completed");

        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            $"/api/traces/{traceId}/replay",
            new
            {
                edits = new[]
                {
                    // sc-274 phase 1: include an output so the preflight assessor's
                    // success-criteria heuristic sees the edit carries new evidence.
                    // The queue-exhaustion behavior is downstream of preflight and
                    // independent of the edit's output content.
                    new
                    {
                        agentKey = "echo",
                        ordinal = 1,
                        decision = "Loop",
                        output = "Routing back to echo to exercise queue-exhaustion behavior.",
                    }
                },
            });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<ReplayResponsePayload>();

        payload.Should().NotBeNull();
        payload!.ReplayState.Should().Be("Failed");
        payload.FailureCode.Should().Be("queue_exhausted");
        payload.ExhaustedAgent.Should().NotBeNull();
        payload.ExhaustedAgent!.AgentKey.Should().Be("echo");
    }

    [Fact]
    public async Task Replay_PrunedArtifact_Returns409Conflict()
    {
        // T2-D: when an artifact referenced by a recorded decision has been pruned, the endpoint
        // surfaces a 409 with a clear message rather than letting the FileNotFoundException
        // bubble up as a 500.
        var traceId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var roundId = Guid.NewGuid();
        var workflowKey = $"replay-pruned-{Guid.NewGuid():N}";

        using (var setupScope = factory.Services.CreateScope())
        {
            var workflowRepo = setupScope.ServiceProvider.GetRequiredService<IWorkflowRepository>();
            var draft = new WorkflowDraft(
                Key: workflowKey,
                Name: "Pruned-artifact test",
                MaxRoundsPerRound: 64,
                Nodes: new[]
                {
                    new WorkflowNodeDraft(
                        Id: Guid.NewGuid(), Kind: WorkflowNodeKind.Start, AgentKey: null, AgentVersion: null,
                        OutputScript: null, OutputPorts: new[] { "Completed" }, LayoutX: 0, LayoutY: 0),
                    new WorkflowNodeDraft(
                        Id: nodeId, Kind: WorkflowNodeKind.Agent, AgentKey: "echo", AgentVersion: 1,
                        OutputScript: null, OutputPorts: new[] { "Completed" }, LayoutX: 100, LayoutY: 0),
                },
                Edges: Array.Empty<WorkflowEdgeDraft>(),
                Inputs: Array.Empty<WorkflowInputDraft>());
            var version = await workflowRepo.CreateNewVersionAsync(draft);

            var db = setupScope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();
            db.WorkflowSagas.Add(new WorkflowSagaStateEntity
            {
                CorrelationId = correlationId,
                TraceId = traceId,
                CurrentState = "Completed",
                CurrentNodeId = nodeId,
                CurrentAgentKey = "echo",
                CurrentRoundId = roundId,
                RoundCount = 1,
                AgentVersionsJson = """{"echo":1}""",
                DecisionHistoryJson = "[]",
                LogicEvaluationHistoryJson = "[]",
                DecisionCount = 1,
                LogicEvaluationCount = 0,
                WorkflowKey = workflowKey,
                WorkflowVersion = version,
                InputsJson = "{}",
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
                Version = 1,
            });
            db.WorkflowSagaDecisions.Add(new WorkflowSagaDecisionEntity
            {
                SagaCorrelationId = correlationId,
                TraceId = traceId,
                Ordinal = 0,
                AgentKey = "echo",
                AgentVersion = 1,
                Decision = "Completed",
                OutputPortName = "Completed",
                OutputRef = "file:///tmp/codeflow-pruned-artifact-does-not-exist.bin",
                NodeId = nodeId,
                RoundId = roundId,
                RecordedAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync($"/api/traces/{traceId}/replay", new { });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Replay_EditWithBadOrdinal_Returns400ValidationProblem()
    {
        var (traceId, _, _, _) = await SeedSimpleEchoTraceAsync();

        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            $"/api/traces/{traceId}/replay",
            new
            {
                edits = new[]
                {
                    // sc-274 phase 1: provide output so the preflight gate passes and
                    // execution reaches the existing admission validator (the ordinal-99
                    // failure path under test).
                    new
                    {
                        agentKey = "echo",
                        ordinal = 99,
                        decision = "Completed",
                        output = "Output to satisfy preflight; ordinal 99 still triggers admission rejection.",
                    }
                },
            });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// sc-274 phase 1 — empty replay request (no edits, no mocks, no version override) is
    /// the round-trip identity case in the existing tests above. With preflight enabled,
    /// it should still succeed because round-trip-identity is a legitimate use case (replays
    /// the trace verbatim) — the preflight goal heuristic only fires when nothing is being
    /// changed AND no override is set. The next test covers the refusal path with a
    /// decision-changing edit that lacks the supporting evidence (output / payload).
    /// </summary>
    [Fact]
    public async Task Replay_DecisionEditWithoutOutput_PreflightRefusesWith422()
    {
        var (traceId, _, _, _) = await SeedSimpleEchoTraceAsync();

        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            $"/api/traces/{traceId}/replay",
            new
            {
                edits = new[]
                {
                    new { agentKey = "echo", ordinal = 1, decision = "Loop" }
                    // ^ no output, no payload — sc-274 success-criteria heuristic fires
                },
            });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var payload = await response.Content.ReadFromJsonAsync<PreflightRefusalPayload>();
        payload.Should().NotBeNull();
        payload!.Code.Should().Be("preflight-ambiguous");
        payload.Mode.Should().Be("ReplayEdit");
        payload.OverallScore.Should().BeLessThan(payload.Threshold);
        payload.ClarificationQuestions.Should().NotBeEmpty()
            .And.Subject.First().Should().Contain("echo/ord-1");
        payload.MissingFields.Should().Contain(f => f.Contains("output"));

        // sc-285: refusal must be persisted to the append-only stream so governance + bundle
        // export both see preflight refusals as first-class evidence.
        using var verifyScope = factory.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();
        var refusal = await db.RefusalEvents.SingleAsync(r => r.TraceId == traceId);
        refusal.Stage.Should().Be("preflight");
        refusal.Code.Should().Be("preflight-ambiguous");
        refusal.DetailJson.Should().NotBeNullOrEmpty();
    }

    /// <summary>
    /// Seeds a workflow with Start → echo (Agent, "Completed"+"Loop" ports) and a saga with one
    /// decision recorded for echo. The "Loop" port is wired back to echo so an edit that flips the
    /// recorded "Completed" to "Loop" extends the run beyond the recorded floor — the
    /// queue-exhaustion test relies on this to exercise the structured-failure surface.
    /// </summary>
    private async Task<(Guid TraceId, Guid CorrelationId, Guid NodeId, string OutputRef)> SeedSimpleEchoTraceAsync(
        string decisionPort = "Completed")
    {
        var startId = Guid.NewGuid();
        var echoId = Guid.NewGuid();
        var workflowKey = $"replay-test-{Guid.NewGuid():N}";

        using var setupScope = factory.Services.CreateScope();
        var workflowRepo = setupScope.ServiceProvider.GetRequiredService<IWorkflowRepository>();
        var artifactStore = setupScope.ServiceProvider.GetRequiredService<IArtifactStore>();

        var draft = new WorkflowDraft(
            Key: workflowKey,
            Name: "Replay test",
            MaxRoundsPerRound: 64,
            Nodes: new[]
            {
                new WorkflowNodeDraft(
                    Id: startId, Kind: WorkflowNodeKind.Start, AgentKey: null, AgentVersion: null,
                    OutputScript: null, OutputPorts: new[] { "Completed" }, LayoutX: 0, LayoutY: 0),
                new WorkflowNodeDraft(
                    Id: echoId, Kind: WorkflowNodeKind.Agent, AgentKey: "echo", AgentVersion: 1,
                    OutputScript: null, OutputPorts: new[] { "Completed", "Loop" }, LayoutX: 100, LayoutY: 0),
            },
            Edges: new[]
            {
                new WorkflowEdgeDraft(startId, "Completed", echoId, "in", false, 0),
                new WorkflowEdgeDraft(echoId, "Loop", echoId, "in", false, 0),
            },
            Inputs: Array.Empty<WorkflowInputDraft>());

        var version = await workflowRepo.CreateNewVersionAsync(draft);

        var artifactTraceId = Guid.NewGuid();
        var artifactRoundId = Guid.NewGuid();
        var inputUri = await artifactStore.WriteAsync(
            new MemoryStream(Encoding.UTF8.GetBytes("seed input")),
            new ArtifactMetadata(
                TraceId: artifactTraceId,
                RoundId: artifactRoundId,
                ArtifactId: Guid.NewGuid(),
                ContentType: "text/plain",
                FileName: "in.txt"));
        var outputUri = await artifactStore.WriteAsync(
            new MemoryStream(Encoding.UTF8.GetBytes("recorded output")),
            new ArtifactMetadata(
                TraceId: artifactTraceId,
                RoundId: artifactRoundId,
                ArtifactId: Guid.NewGuid(),
                ContentType: "text/plain",
                FileName: "out.txt"));

        var traceId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var roundId = Guid.NewGuid();

        var db = setupScope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();
        var saga = new WorkflowSagaStateEntity
        {
            CorrelationId = correlationId,
            TraceId = traceId,
            CurrentState = "Completed",
            CurrentNodeId = echoId,
            CurrentAgentKey = "echo",
            CurrentRoundId = roundId,
            RoundCount = 1,
            AgentVersionsJson = """{"echo":1}""",
            DecisionHistoryJson = "[]",
            LogicEvaluationHistoryJson = "[]",
            DecisionCount = 1,
            LogicEvaluationCount = 0,
            WorkflowKey = workflowKey,
            WorkflowVersion = version,
            InputsJson = "{}",
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
            Version = 1,
        };
        db.WorkflowSagas.Add(saga);
        db.WorkflowSagaDecisions.Add(new WorkflowSagaDecisionEntity
        {
            SagaCorrelationId = correlationId,
            TraceId = traceId,
            Ordinal = 0,
            AgentKey = "echo",
            AgentVersion = 1,
            Decision = decisionPort,
            OutputPortName = decisionPort,
            InputRef = inputUri.ToString(),
            OutputRef = outputUri.ToString(),
            NodeId = echoId,
            RoundId = roundId,
            RecordedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        return (traceId, correlationId, echoId, outputUri.ToString());
    }

    private sealed record ReplayResponsePayload(
        Guid OriginalTraceId,
        string ReplayState,
        string? ReplayTerminalPort,
        string? FailureReason,
        string? FailureCode,
        ReplayExhaustedAgentPayload? ExhaustedAgent,
        IReadOnlyList<RecordedDecisionRefPayload> Decisions,
        IReadOnlyList<ReplayEventPayload> ReplayEvents,
        ReplayDriftPayload Drift);

    private sealed record ReplayDriftPayload(string Level, IReadOnlyList<string> Warnings);
    private sealed record ReplayExhaustedAgentPayload(string AgentKey, int RecordedResponses);
    private sealed record RecordedDecisionRefPayload(
        string AgentKey,
        int OrdinalPerAgent,
        Guid SagaCorrelationId,
        int SagaOrdinal,
        Guid? NodeId,
        Guid RoundId,
        string OriginalDecision);
    private sealed record ReplayEventPayload(int Ordinal, string Kind, Guid NodeId, string NodeKind);

    private sealed record PreflightRefusalPayload(
        Guid OriginalTraceId,
        string Code,
        string Mode,
        double OverallScore,
        double Threshold,
        IReadOnlyList<PreflightDimensionPayload> Dimensions,
        IReadOnlyList<string> MissingFields,
        IReadOnlyList<string> ClarificationQuestions);

    private sealed record PreflightDimensionPayload(string Dimension, double Score, string? Reason);
}
