using CodeFlow.Orchestration.DryRun;
using CodeFlow.Orchestration.Replay;
using CodeFlow.Orchestration.Scripting;
using CodeFlow.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;

namespace CodeFlow.Orchestration.Tests.Replay;

/// <summary>
/// End-to-end orchestration-level coverage of the replay pipeline: extract recorded decisions →
/// apply edits → run DryRunExecutor. These tests do not cross the API boundary; the API
/// integration test exercises the endpoint plumbing separately.
/// </summary>
public sealed class ReplayPipelineTests
{
    private static readonly Guid OuterStartId = Guid.Parse("eeee1111-1111-1111-1111-eeeeeeeeeeee");
    private static readonly Guid ReviewLoopId = Guid.Parse("eeee2222-2222-2222-2222-eeeeeeeeeeee");
    private static readonly Guid InnerStartId = Guid.Parse("eeee3333-3333-3333-3333-eeeeeeeeeeee");
    private static readonly Guid ProducerId = Guid.Parse("eeee4444-4444-4444-4444-eeeeeeeeeeee");
    private static readonly Guid ReviewerId = Guid.Parse("eeee5555-5555-5555-5555-eeeeeeeeeeee");

    /// <summary>
    /// Card acceptance: round-trip identity. Run a 3-round all-rejected ReviewLoop saga, extract
    /// decisions, replay with no edits, and confirm the executor still terminates Exhausted with
    /// the same per-agent counts (3 producer + 3 reviewer invocations).
    /// </summary>
    [Fact]
    public async Task RoundTripIdentity_NoEdits_ReproducesExhaustedTerminal()
    {
        var (outer, inner) = BuildReviewLoopPair(maxRounds: 3);
        var (parentSaga, childSagas, decisions, store) = SeedExhaustedThreeRoundLoop();

        var bundle = await ReplayMockExtractor.ExtractAsync(
            parentSaga,
            new[] { parentSaga }.Concat(childSagas).ToArray(),
            decisions,
            store,
            CancellationToken.None);

        bundle.Mocks["producer"].Should().HaveCount(3);
        bundle.Mocks["reviewer"].Should().HaveCount(3);
        bundle.Mocks["reviewer"].Select(m => m.Decision).Should().AllBe("Rejected");

        var ports = await ReplayEditsApplicator.BuildPortIndexAsync(
            outer, new MultiWorkflowFakeRepository(outer, inner), CancellationToken.None);
        var applied = ReplayEditsApplicator.Apply(
            bundle.Mocks, edits: null, additionalMocks: null, ports, $"'{outer.Key}' v{outer.Version}");
        applied.ValidationErrors.Should().BeEmpty();

        var executor = new DryRunExecutor(
            new MultiWorkflowFakeRepository(outer, inner),
            new LogicNodeScriptHost(new MemoryCache(new MemoryCacheOptions())));

        var result = await executor.ExecuteAsync(
            new DryRunRequest("outer", null, "initial PRD", applied.Mocks),
            CancellationToken.None);

        result.State.Should().Be(DryRunTerminalState.Completed);
        result.TerminalPort.Should().Be("Exhausted");
        result.Events.Count(e => e.Kind == DryRunEventKind.LoopIteration).Should().Be(3);
    }

    /// <summary>
    /// Card acceptance: single edit at round 3 reviewer flips Exhausted → Approved.
    /// </summary>
    [Fact]
    public async Task SingleEditAtRound3Reviewer_FlipsExhaustedToApproved()
    {
        var (outer, inner) = BuildReviewLoopPair(maxRounds: 3);
        var (parentSaga, childSagas, decisions, store) = SeedExhaustedThreeRoundLoop();

        var bundle = await ReplayMockExtractor.ExtractAsync(
            parentSaga,
            new[] { parentSaga }.Concat(childSagas).ToArray(),
            decisions,
            store,
            CancellationToken.None);

        // Validate ordinals are 1-based per agent.
        bundle.Decisions
            .Where(d => d.AgentKey == "reviewer")
            .Select(d => d.OrdinalPerAgent)
            .Should().Equal(1, 2, 3);

        var edits = new[]
        {
            new ReplayEdit("reviewer", Ordinal: 3, Decision: "Approved", Output: "ship it", Payload: null),
        };

        var ports = await ReplayEditsApplicator.BuildPortIndexAsync(
            outer, new MultiWorkflowFakeRepository(outer, inner), CancellationToken.None);
        var applied = ReplayEditsApplicator.Apply(
            bundle.Mocks, edits, additionalMocks: null, ports, $"'{outer.Key}' v{outer.Version}");
        applied.ValidationErrors.Should().BeEmpty();
        applied.Mocks["reviewer"][2].Decision.Should().Be("Approved");

        var executor = new DryRunExecutor(
            new MultiWorkflowFakeRepository(outer, inner),
            new LogicNodeScriptHost(new MemoryCache(new MemoryCacheOptions())));

        var result = await executor.ExecuteAsync(
            new DryRunRequest("outer", null, "initial PRD", applied.Mocks),
            CancellationToken.None);

        result.State.Should().Be(DryRunTerminalState.Completed);
        result.TerminalPort.Should().Be("Approved");
    }

    /// <summary>
    /// Card acceptance: lengthening edit without additionalMocks fails with a queue_exhausted
    /// surface; supplying enough additional mocks lets it complete.
    /// </summary>
    [Fact]
    public async Task LengtheningEdit_FailsWithQueueExhaustionWithoutAdditionalMocks()
    {
        var (outer, inner) = BuildReviewLoopPair(maxRounds: 5);
        var (parentSaga, childSagas, decisions, store) = SeedTwoRoundLoopApproved();

        var bundle = await ReplayMockExtractor.ExtractAsync(
            parentSaga,
            new[] { parentSaga }.Concat(childSagas).ToArray(),
            decisions,
            store,
            CancellationToken.None);

        // Flip the round-2 reviewer Approval back to Rejected. The recording only had 2 producer
        // and 2 reviewer responses, so the loop now needs a round 3 producer (no recording).
        var edits = new[]
        {
            new ReplayEdit("reviewer", Ordinal: 2, Decision: "Rejected", Output: "not yet", Payload: null),
        };

        var ports = await ReplayEditsApplicator.BuildPortIndexAsync(
            outer, new MultiWorkflowFakeRepository(outer, inner), CancellationToken.None);
        var applied = ReplayEditsApplicator.Apply(
            bundle.Mocks, edits, additionalMocks: null, ports, $"'{outer.Key}' v{outer.Version}");
        applied.ValidationErrors.Should().BeEmpty();

        var executor = new DryRunExecutor(
            new MultiWorkflowFakeRepository(outer, inner),
            new LogicNodeScriptHost(new MemoryCache(new MemoryCacheOptions())));

        var result = await executor.ExecuteAsync(
            new DryRunRequest("outer", null, "initial PRD", applied.Mocks),
            CancellationToken.None);

        result.State.Should().Be(DryRunTerminalState.Failed);
        result.FailureReason.Should().StartWith("No mock response queued for agent ");

        // With sufficient additional mocks the same edit replays cleanly.
        var withAdditional = ReplayEditsApplicator.Apply(
            bundle.Mocks,
            edits,
            additionalMocks: new Dictionary<string, IReadOnlyList<DryRunMockResponse>>
            {
                ["producer"] = new[] { new DryRunMockResponse("Completed", "extra draft", null) },
                ["reviewer"] = new[] { new DryRunMockResponse("Approved", "ship it", null) },
            },
            ports,
            $"'{outer.Key}' v{outer.Version}");

        var success = await executor.ExecuteAsync(
            new DryRunRequest("outer", null, "initial PRD", withAdditional.Mocks),
            CancellationToken.None);

        success.State.Should().Be(DryRunTerminalState.Completed);
        success.TerminalPort.Should().Be("Approved");
    }

    // ------------------- fixture builders -------------------

    private static (Workflow Outer, Workflow Inner) BuildReviewLoopPair(int maxRounds)
    {
        var inner = new Workflow(
            Key: "inner",
            Version: 1,
            Name: "Inner ReviewLoop body",
            MaxRoundsPerRound: 64,
            CreatedAtUtc: DateTime.UtcNow,
            Nodes: new[]
            {
                new WorkflowNode(
                    Id: InnerStartId, Kind: WorkflowNodeKind.Start, AgentKey: null, AgentVersion: null,
                    OutputScript: null, OutputPorts: new[] { "Completed" }, LayoutX: 0, LayoutY: 0),
                new WorkflowNode(
                    Id: ProducerId, Kind: WorkflowNodeKind.Agent, AgentKey: "producer", AgentVersion: 1,
                    OutputScript: null, OutputPorts: new[] { "Completed" }, LayoutX: 100, LayoutY: 0),
                new WorkflowNode(
                    Id: ReviewerId, Kind: WorkflowNodeKind.Agent, AgentKey: "reviewer", AgentVersion: 1,
                    OutputScript: null, OutputPorts: new[] { "Approved", "Rejected" }, LayoutX: 200, LayoutY: 0),
            },
            Edges: new[]
            {
                new WorkflowEdge(InnerStartId, "Completed", ProducerId, "in", false, 0),
                new WorkflowEdge(ProducerId, "Completed", ReviewerId, "in", false, 0),
            },
            Inputs: Array.Empty<WorkflowInput>());

        var outer = new Workflow(
            Key: "outer",
            Version: 1,
            Name: "Outer with ReviewLoop",
            MaxRoundsPerRound: 64,
            CreatedAtUtc: DateTime.UtcNow,
            Nodes: new[]
            {
                new WorkflowNode(
                    Id: OuterStartId, Kind: WorkflowNodeKind.Start, AgentKey: null, AgentVersion: null,
                    OutputScript: null, OutputPorts: new[] { "Completed" }, LayoutX: 0, LayoutY: 0),
                new WorkflowNode(
                    Id: ReviewLoopId, Kind: WorkflowNodeKind.ReviewLoop, AgentKey: null, AgentVersion: null,
                    OutputScript: null, OutputPorts: new[] { "Approved", "Exhausted" },
                    LayoutX: 100, LayoutY: 0,
                    SubflowKey: "inner", SubflowVersion: 1,
                    ReviewMaxRounds: maxRounds, LoopDecision: "Rejected"),
            },
            Edges: new[]
            {
                new WorkflowEdge(OuterStartId, "Completed", ReviewLoopId, "in", false, 0),
            },
            Inputs: Array.Empty<WorkflowInput>());

        return (outer, inner);
    }

    private static (
        WorkflowSagaStateEntity Parent,
        WorkflowSagaStateEntity[] Children,
        WorkflowSagaDecisionEntity[] Decisions,
        InMemoryArtifactStore Store) SeedExhaustedThreeRoundLoop()
    {
        var store = new InMemoryArtifactStore();
        var parentCorr = Guid.NewGuid();
        var parentTrace = Guid.NewGuid();

        var children = new WorkflowSagaStateEntity[3];
        var decisions = new List<WorkflowSagaDecisionEntity>();
        var parentDecisions = new List<WorkflowSagaDecisionEntity>();

        for (var round = 0; round < 3; round++)
        {
            var childCorr = Guid.NewGuid();
            var childTrace = Guid.NewGuid();
            var childRoundId = Guid.NewGuid();

            children[round] = new WorkflowSagaStateEntity
            {
                CorrelationId = childCorr,
                TraceId = childTrace,
                CurrentState = "Completed",
                CurrentNodeId = ReviewerId,
                CurrentAgentKey = "reviewer",
                CurrentRoundId = childRoundId,
                RoundCount = 1,
                AgentVersionsJson = "{}",
                DecisionHistoryJson = "[]",
                LogicEvaluationHistoryJson = "[]",
                DecisionCount = 2,
                LogicEvaluationCount = 0,
                WorkflowKey = "inner",
                WorkflowVersion = 1,
                InputsJson = "{}",
                ParentTraceId = parentTrace,
                ParentNodeId = ReviewLoopId,
                ParentRoundId = childRoundId,
                ParentReviewRound = round + 1,
                ParentReviewMaxRounds = 3,
                SubflowDepth = 1,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
                Version = 1,
            };

            var producerOut = $"memory://r{round + 1}-producer";
            var reviewerOut = $"memory://r{round + 1}-reviewer";
            store.Seed(producerOut, $"draft v{round + 1}");
            store.Seed(reviewerOut, $"reject reason {round + 1}");

            decisions.Add(NewDecision(childCorr, childTrace, ordinal: 0, agentKey: "producer",
                decision: "Completed", outputRef: producerOut, nodeId: ProducerId, roundId: childRoundId));
            decisions.Add(NewDecision(childCorr, childTrace, ordinal: 1, agentKey: "reviewer",
                decision: "Rejected", outputRef: reviewerOut, nodeId: ReviewerId, roundId: childRoundId));

            parentDecisions.Add(NewDecision(parentCorr, parentTrace, ordinal: round, agentKey: "review-loop:inner",
                decision: "Rejected", outputRef: null, nodeId: ReviewLoopId, roundId: childRoundId));
        }

        var parent = new WorkflowSagaStateEntity
        {
            CorrelationId = parentCorr,
            TraceId = parentTrace,
            CurrentState = "Completed",
            CurrentNodeId = ReviewLoopId,
            CurrentAgentKey = string.Empty,
            CurrentRoundId = Guid.NewGuid(),
            RoundCount = 3,
            AgentVersionsJson = """{"producer":1,"reviewer":1}""",
            DecisionHistoryJson = "[]",
            LogicEvaluationHistoryJson = "[]",
            DecisionCount = parentDecisions.Count,
            LogicEvaluationCount = 0,
            WorkflowKey = "outer",
            WorkflowVersion = 1,
            InputsJson = "{}",
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
            Version = 1,
        };

        decisions.AddRange(parentDecisions);

        return (parent, children, decisions.ToArray(), store);
    }

    private static (
        WorkflowSagaStateEntity Parent,
        WorkflowSagaStateEntity[] Children,
        WorkflowSagaDecisionEntity[] Decisions,
        InMemoryArtifactStore Store) SeedTwoRoundLoopApproved()
    {
        var store = new InMemoryArtifactStore();
        var parentCorr = Guid.NewGuid();
        var parentTrace = Guid.NewGuid();

        var children = new WorkflowSagaStateEntity[2];
        var decisions = new List<WorkflowSagaDecisionEntity>();
        var parentDecisions = new List<WorkflowSagaDecisionEntity>();

        for (var round = 0; round < 2; round++)
        {
            var childCorr = Guid.NewGuid();
            var childTrace = Guid.NewGuid();
            var childRoundId = Guid.NewGuid();

            children[round] = new WorkflowSagaStateEntity
            {
                CorrelationId = childCorr,
                TraceId = childTrace,
                CurrentState = "Completed",
                CurrentNodeId = ReviewerId,
                CurrentAgentKey = "reviewer",
                CurrentRoundId = childRoundId,
                RoundCount = 1,
                AgentVersionsJson = "{}",
                DecisionHistoryJson = "[]",
                LogicEvaluationHistoryJson = "[]",
                DecisionCount = 2,
                LogicEvaluationCount = 0,
                WorkflowKey = "inner",
                WorkflowVersion = 1,
                InputsJson = "{}",
                ParentTraceId = parentTrace,
                ParentNodeId = ReviewLoopId,
                ParentRoundId = childRoundId,
                ParentReviewRound = round + 1,
                ParentReviewMaxRounds = 5,
                SubflowDepth = 1,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
                Version = 1,
            };

            var producerOut = $"memory://r{round + 1}-producer";
            var reviewerOut = $"memory://r{round + 1}-reviewer";
            store.Seed(producerOut, $"draft v{round + 1}");
            store.Seed(reviewerOut, round == 1 ? "ship it" : $"more work {round + 1}");

            decisions.Add(NewDecision(childCorr, childTrace, ordinal: 0, agentKey: "producer",
                decision: "Completed", outputRef: producerOut, nodeId: ProducerId, roundId: childRoundId));
            decisions.Add(NewDecision(childCorr, childTrace, ordinal: 1, agentKey: "reviewer",
                decision: round == 1 ? "Approved" : "Rejected",
                outputRef: reviewerOut, nodeId: ReviewerId, roundId: childRoundId));

            parentDecisions.Add(NewDecision(parentCorr, parentTrace, ordinal: round, agentKey: "review-loop:inner",
                decision: round == 1 ? "Approved" : "Rejected",
                outputRef: null, nodeId: ReviewLoopId, roundId: childRoundId));
        }

        var parent = new WorkflowSagaStateEntity
        {
            CorrelationId = parentCorr,
            TraceId = parentTrace,
            CurrentState = "Completed",
            CurrentNodeId = ReviewLoopId,
            CurrentAgentKey = string.Empty,
            CurrentRoundId = Guid.NewGuid(),
            RoundCount = 2,
            AgentVersionsJson = """{"producer":1,"reviewer":1}""",
            DecisionHistoryJson = "[]",
            LogicEvaluationHistoryJson = "[]",
            DecisionCount = parentDecisions.Count,
            LogicEvaluationCount = 0,
            WorkflowKey = "outer",
            WorkflowVersion = 1,
            InputsJson = "{}",
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
            Version = 1,
        };

        decisions.AddRange(parentDecisions);

        return (parent, children, decisions.ToArray(), store);
    }

    private static WorkflowSagaDecisionEntity NewDecision(
        Guid sagaCorrelationId,
        Guid traceId,
        int ordinal,
        string agentKey,
        string decision,
        string? outputRef,
        Guid? nodeId = null,
        Guid? roundId = null) =>
        new()
        {
            SagaCorrelationId = sagaCorrelationId,
            TraceId = traceId,
            Ordinal = ordinal,
            AgentKey = agentKey,
            AgentVersion = 1,
            Decision = decision,
            OutputRef = outputRef,
            NodeId = nodeId ?? Guid.NewGuid(),
            RoundId = roundId ?? Guid.NewGuid(),
            RecordedAtUtc = DateTime.UtcNow,
            OutputPortName = decision,
        };

    private sealed class MultiWorkflowFakeRepository : IWorkflowRepository
    {
        private readonly Dictionary<string, Workflow> byKey;

        public MultiWorkflowFakeRepository(params Workflow[] workflows)
        {
            byKey = workflows.ToDictionary(w => w.Key, StringComparer.Ordinal);
        }

        public Task<Workflow> GetAsync(string key, int version, CancellationToken cancellationToken = default) =>
            byKey.TryGetValue(key, out var w)
                ? Task.FromResult(w)
                : throw new InvalidOperationException($"Unknown workflow '{key}' v{version}.");

        public Task<Workflow?> GetLatestAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(byKey.TryGetValue(key, out var w) ? w : null);

        public Task<IReadOnlyList<Workflow>> ListLatestAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Workflow>>(byKey.Values.ToArray());

        public Task<IReadOnlyList<Workflow>> ListVersionsAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Workflow>>(byKey.TryGetValue(key, out var w) ? new[] { w } : Array.Empty<Workflow>());

        public Task<WorkflowEdge?> FindNextAsync(string key, int version, Guid fromNodeId, string outputPortName, CancellationToken cancellationToken = default) =>
            Task.FromResult(byKey[key].FindNext(fromNodeId, outputPortName));

        public Task<IReadOnlyCollection<string>> GetTerminalPortsAsync(string key, int version, CancellationToken cancellationToken = default) =>
            Task.FromResult(byKey[key].TerminalPorts);

        public Task<int> CreateNewVersionAsync(WorkflowDraft draft, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
