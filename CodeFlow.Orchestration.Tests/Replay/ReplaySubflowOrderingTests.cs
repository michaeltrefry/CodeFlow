using CodeFlow.Orchestration.DryRun;
using CodeFlow.Orchestration.Replay;
using CodeFlow.Orchestration.Scripting;
using CodeFlow.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;

namespace CodeFlow.Orchestration.Tests.Replay;

/// <summary>
/// T2-D coverage for the cross-saga mock-queue ordering rule. The dry-run executor's mock queues
/// are global per agent key; when the same agent is invoked across multiple subflow boundaries or
/// ReviewLoop iterations, recordings must be queued in the order DryRunExecutor will dequeue them
/// — saga-traversal order across the parent + descendant sagas.
/// </summary>
public sealed class ReplaySubflowOrderingTests
{
    private static readonly Guid OuterStartId = Guid.Parse("dddd1111-1111-1111-1111-dddddddddddd");
    private static readonly Guid SubflowAId = Guid.Parse("dddd2222-2222-2222-2222-dddddddddddd");
    private static readonly Guid SubflowBId = Guid.Parse("dddd3333-3333-3333-3333-dddddddddddd");
    private static readonly Guid InnerStartId = Guid.Parse("dddd4444-4444-4444-4444-dddddddddddd");
    private static readonly Guid InnerProducerId = Guid.Parse("dddd5555-5555-5555-5555-dddddddddddd");
    private static readonly Guid ReviewLoopId = Guid.Parse("dddd6666-6666-6666-6666-dddddddddddd");
    private static readonly Guid ReviewerId = Guid.Parse("dddd7777-7777-7777-7777-dddddddddddd");

    /// <summary>
    /// T2-D test #1: a single ReviewLoop with three rejected rounds. The reviewer queue must hold
    /// three Rejected entries in round order, and replay must reproduce Exhausted with no edits.
    /// </summary>
    [Fact]
    public async Task SingleReviewLoop_ThreeRejectedRounds_QueueOrderMatchesRoundOrder()
    {
        var (outer, inner) = BuildReviewLoopPair(maxRounds: 3);
        var (parent, children, decisions, store) = SeedThreeRoundReviewLoop();

        var bundle = await ReplayMockExtractor.ExtractAsync(
            parent, new[] { parent }.Concat(children).ToArray(), decisions, store, CancellationToken.None);

        bundle.Mocks["reviewer"].Should().HaveCount(3);
        bundle.Mocks["reviewer"].Select(m => (m.Decision, m.Output))
            .Should().Equal(
                ("Rejected", "round-1 reviewer"),
                ("Rejected", "round-2 reviewer"),
                ("Rejected", "round-3 reviewer"));

        bundle.Mocks["producer"].Should().HaveCount(3);
        bundle.Mocks["producer"].Select(m => m.Output)
            .Should().Equal("round-1 producer", "round-2 producer", "round-3 producer");

        var ports = await ReplayEditsApplicator.BuildPortIndexAsync(
            outer, new SimpleRepository(outer, inner), CancellationToken.None);
        var applied = ReplayEditsApplicator.Apply(
            bundle.Mocks, edits: null, additionalMocks: null, ports, $"'{outer.Key}' v{outer.Version}");
        applied.ValidationErrors.Should().BeEmpty();

        var executor = new DryRunExecutor(
            new SimpleRepository(outer, inner),
            new LogicNodeScriptHost(new MemoryCache(new MemoryCacheOptions())));
        var result = await executor.ExecuteAsync(
            new DryRunRequest("outer", null, "seed", applied.Mocks),
            CancellationToken.None);

        result.State.Should().Be(DryRunTerminalState.Completed);
        result.TerminalPort.Should().Be("Exhausted");
    }

    /// <summary>
    /// T2-D test #2: two sibling Subflow nodes both invoking the same agent. The mock queue for
    /// that agent must thread responses in saga-traversal order — A's recording first, then B's.
    /// </summary>
    [Fact]
    public async Task TwoSiblingSubflows_SameAgent_QueuedInSagaTraversalOrder()
    {
        var (outer, inner) = BuildTwoSiblingSubflows();
        var (parent, childA, childB, decisions, store) = SeedTwoSiblingSubflowSagas();

        var bundle = await ReplayMockExtractor.ExtractAsync(
            parent, new[] { parent, childA, childB }, decisions, store, CancellationToken.None);

        bundle.Mocks.Should().ContainKey("producer").WhoseValue.Should().HaveCount(2);
        bundle.Mocks["producer"][0].Output.Should().Be("from subflow A");
        bundle.Mocks["producer"][1].Output.Should().Be("from subflow B");

        // Replay-with-no-edits should run cleanly through both subflows.
        var ports = await ReplayEditsApplicator.BuildPortIndexAsync(
            outer, new SimpleRepository(outer, inner), CancellationToken.None);
        var applied = ReplayEditsApplicator.Apply(
            bundle.Mocks, edits: null, additionalMocks: null, ports, $"'{outer.Key}' v{outer.Version}");
        applied.ValidationErrors.Should().BeEmpty();

        var executor = new DryRunExecutor(
            new SimpleRepository(outer, inner),
            new LogicNodeScriptHost(new MemoryCache(new MemoryCacheOptions())));
        var result = await executor.ExecuteAsync(
            new DryRunRequest("two-subflows", null, "seed", applied.Mocks),
            CancellationToken.None);

        result.State.Should().Be(DryRunTerminalState.Completed);
        // Both subflows fired exactly once each (each producer mock was consumed).
        result.Events.Count(e => e.Kind == DryRunEventKind.SubflowEntered).Should().Be(2);
    }

    /// <summary>
    /// T2-D test #3: synthetic subflow/ReviewLoop decisions never appear as mock queue entries —
    /// the parent's "subflow:..." / "review-loop:..." rows are filtered before queueing.
    /// </summary>
    [Fact]
    public async Task SyntheticDecisions_NeverAppearInMockQueue()
    {
        var (parent, children, decisions, store) = SeedThreeRoundReviewLoop();

        var bundle = await ReplayMockExtractor.ExtractAsync(
            parent, new[] { parent }.Concat(children).ToArray(), decisions, store, CancellationToken.None);

        bundle.Mocks.Keys.Should().NotContain(k =>
            k == "subflow"
            || k == "review-loop"
            || k.StartsWith("subflow:", StringComparison.Ordinal)
            || k.StartsWith("review-loop:", StringComparison.Ordinal));

        // The parent saga had 3 synthetic decisions but they don't appear in `bundle.Decisions`
        // either — `RecordedDecisionRef` only tracks responses an author can edit.
        bundle.Decisions.Should().NotContain(d =>
            ReplayMockExtractor.IsSyntheticSubflowAgentKey(d.AgentKey));
    }

    /// <summary>
    /// T2-D test #4: when an artifact referenced by a recorded decision has been pruned (e.g. by
    /// trace deletion or storage retention), the extractor surfaces a FileNotFoundException rather
    /// than silently dropping the round. The endpoint translates this into a 409.
    /// </summary>
    [Fact]
    public async Task PrunedArtifact_PropagatesFileNotFoundException()
    {
        var corr = Guid.NewGuid();
        var trace = Guid.NewGuid();
        var saga = NewSimpleSaga(corr, trace);

        var store = new InMemoryArtifactStore();
        // Decision references an output URI that was never seeded — simulates a pruned artifact.
        var decisions = new[]
        {
            new WorkflowSagaDecisionEntity
            {
                SagaCorrelationId = corr,
                TraceId = trace,
                Ordinal = 0,
                AgentKey = "echo",
                AgentVersion = 1,
                Decision = "Completed",
                OutputRef = "memory://pruned-artifact",
                NodeId = Guid.NewGuid(),
                RoundId = Guid.NewGuid(),
                RecordedAtUtc = DateTime.UtcNow,
                OutputPortName = "Completed",
            },
        };

        var act = () => ReplayMockExtractor.ExtractAsync(
            saga, new[] { saga }, decisions, store, CancellationToken.None);

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    // --------- fixture builders ---------

    private static (Workflow Outer, Workflow Inner) BuildReviewLoopPair(int maxRounds)
    {
        var inner = new Workflow(
            Key: "inner",
            Version: 1,
            Name: "inner body",
            MaxRoundsPerRound: 64,
            CreatedAtUtc: DateTime.UtcNow,
            Nodes: new[]
            {
                new WorkflowNode(
                    Id: InnerStartId, Kind: WorkflowNodeKind.Start, AgentKey: null, AgentVersion: null,
                    OutputScript: null, OutputPorts: new[] { "Completed" }, LayoutX: 0, LayoutY: 0),
                new WorkflowNode(
                    Id: InnerProducerId, Kind: WorkflowNodeKind.Agent, AgentKey: "producer", AgentVersion: 1,
                    OutputScript: null, OutputPorts: new[] { "Completed" }, LayoutX: 100, LayoutY: 0),
                new WorkflowNode(
                    Id: ReviewerId, Kind: WorkflowNodeKind.Agent, AgentKey: "reviewer", AgentVersion: 1,
                    OutputScript: null, OutputPorts: new[] { "Approved", "Rejected" }, LayoutX: 200, LayoutY: 0),
            },
            Edges: new[]
            {
                new WorkflowEdge(InnerStartId, "Completed", InnerProducerId, "in", false, 0),
                new WorkflowEdge(InnerProducerId, "Completed", ReviewerId, "in", false, 0),
            },
            Inputs: Array.Empty<WorkflowInput>());

        var outer = new Workflow(
            Key: "outer",
            Version: 1,
            Name: "outer",
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

    private static (Workflow Outer, Workflow Inner) BuildTwoSiblingSubflows()
    {
        // Inner body: Start → producer → end. Reused by both Subflow nodes.
        var inner = new Workflow(
            Key: "inner",
            Version: 1,
            Name: "shared body",
            MaxRoundsPerRound: 64,
            CreatedAtUtc: DateTime.UtcNow,
            Nodes: new[]
            {
                new WorkflowNode(
                    Id: InnerStartId, Kind: WorkflowNodeKind.Start, AgentKey: null, AgentVersion: null,
                    OutputScript: null, OutputPorts: new[] { "Completed" }, LayoutX: 0, LayoutY: 0),
                new WorkflowNode(
                    Id: InnerProducerId, Kind: WorkflowNodeKind.Agent, AgentKey: "producer", AgentVersion: 1,
                    OutputScript: null, OutputPorts: new[] { "Completed" }, LayoutX: 100, LayoutY: 0),
            },
            Edges: new[]
            {
                new WorkflowEdge(InnerStartId, "Completed", InnerProducerId, "in", false, 0),
            },
            Inputs: Array.Empty<WorkflowInput>());

        var outer = new Workflow(
            Key: "two-subflows",
            Version: 1,
            Name: "outer with two siblings",
            MaxRoundsPerRound: 64,
            CreatedAtUtc: DateTime.UtcNow,
            Nodes: new[]
            {
                new WorkflowNode(
                    Id: OuterStartId, Kind: WorkflowNodeKind.Start, AgentKey: null, AgentVersion: null,
                    OutputScript: null, OutputPorts: new[] { "Completed" }, LayoutX: 0, LayoutY: 0),
                new WorkflowNode(
                    Id: SubflowAId, Kind: WorkflowNodeKind.Subflow, AgentKey: null, AgentVersion: null,
                    OutputScript: null, OutputPorts: new[] { "Completed" }, LayoutX: 100, LayoutY: 0,
                    SubflowKey: "inner", SubflowVersion: 1),
                new WorkflowNode(
                    Id: SubflowBId, Kind: WorkflowNodeKind.Subflow, AgentKey: null, AgentVersion: null,
                    OutputScript: null, OutputPorts: new[] { "Completed" }, LayoutX: 200, LayoutY: 0,
                    SubflowKey: "inner", SubflowVersion: 1),
            },
            Edges: new[]
            {
                new WorkflowEdge(OuterStartId, "Completed", SubflowAId, "in", false, 0),
                new WorkflowEdge(SubflowAId, "Completed", SubflowBId, "in", false, 0),
            },
            Inputs: Array.Empty<WorkflowInput>());

        return (outer, inner);
    }

    private static (
        WorkflowSagaStateEntity Parent,
        WorkflowSagaStateEntity[] Children,
        WorkflowSagaDecisionEntity[] Decisions,
        InMemoryArtifactStore Store) SeedThreeRoundReviewLoop()
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

            children[round] = NewChildSaga(childCorr, childTrace, parentTrace, ReviewLoopId, childRoundId, round + 1, 3);

            var producerOut = $"memory://r{round + 1}-producer";
            var reviewerOut = $"memory://r{round + 1}-reviewer";
            store.Seed(producerOut, $"round-{round + 1} producer");
            store.Seed(reviewerOut, $"round-{round + 1} reviewer");

            decisions.Add(NewDecision(childCorr, childTrace, 0, "producer", "Completed", producerOut, InnerProducerId, childRoundId));
            decisions.Add(NewDecision(childCorr, childTrace, 1, "reviewer", "Rejected", reviewerOut, ReviewerId, childRoundId));
            parentDecisions.Add(NewDecision(parentCorr, parentTrace, round, "review-loop:inner", "Rejected", null, ReviewLoopId, childRoundId));
        }

        var parent = NewParentSaga(parentCorr, parentTrace, "outer", parentDecisions.Count);
        decisions.AddRange(parentDecisions);

        return (parent, children, decisions.ToArray(), store);
    }

    private static (
        WorkflowSagaStateEntity Parent,
        WorkflowSagaStateEntity ChildA,
        WorkflowSagaStateEntity ChildB,
        WorkflowSagaDecisionEntity[] Decisions,
        InMemoryArtifactStore Store) SeedTwoSiblingSubflowSagas()
    {
        var store = new InMemoryArtifactStore();
        var parentCorr = Guid.NewGuid();
        var parentTrace = Guid.NewGuid();

        var aCorr = Guid.NewGuid();
        var aTrace = Guid.NewGuid();
        var aRoundId = Guid.NewGuid();
        var bCorr = Guid.NewGuid();
        var bTrace = Guid.NewGuid();
        var bRoundId = Guid.NewGuid();

        var childA = NewChildSaga(aCorr, aTrace, parentTrace, SubflowAId, aRoundId, parentReviewRound: null, parentReviewMaxRounds: null);
        var childB = NewChildSaga(bCorr, bTrace, parentTrace, SubflowBId, bRoundId, parentReviewRound: null, parentReviewMaxRounds: null);

        store.Seed("memory://producer-a", "from subflow A");
        store.Seed("memory://producer-b", "from subflow B");

        var decisions = new[]
        {
            NewDecision(aCorr, aTrace, 0, "producer", "Completed", "memory://producer-a", InnerProducerId, aRoundId),
            NewDecision(bCorr, bTrace, 0, "producer", "Completed", "memory://producer-b", InnerProducerId, bRoundId),
            // Parent's decisions: synthetic A first, synthetic B second — saga-traversal order.
            NewDecision(parentCorr, parentTrace, 0, "subflow:inner", "Completed", null, SubflowAId, aRoundId),
            NewDecision(parentCorr, parentTrace, 1, "subflow:inner", "Completed", null, SubflowBId, bRoundId),
        };

        var parent = NewParentSaga(parentCorr, parentTrace, "two-subflows", decisionCount: 2);

        return (parent, childA, childB, decisions, store);
    }

    private static WorkflowSagaStateEntity NewParentSaga(Guid corr, Guid trace, string workflowKey, int decisionCount)
    {
        var now = DateTime.UtcNow;
        return new WorkflowSagaStateEntity
        {
            CorrelationId = corr,
            TraceId = trace,
            CurrentState = "Completed",
            CurrentNodeId = OuterStartId,
            CurrentAgentKey = string.Empty,
            CurrentRoundId = Guid.NewGuid(),
            RoundCount = decisionCount,
            AgentVersionsJson = """{"producer":1,"reviewer":1}""",
            DecisionHistoryJson = "[]",
            LogicEvaluationHistoryJson = "[]",
            DecisionCount = decisionCount,
            LogicEvaluationCount = 0,
            WorkflowKey = workflowKey,
            WorkflowVersion = 1,
            InputsJson = "{}",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Version = 1,
        };
    }

    private static WorkflowSagaStateEntity NewChildSaga(
        Guid corr, Guid trace, Guid parentTrace, Guid parentNode, Guid parentRound,
        int? parentReviewRound, int? parentReviewMaxRounds)
    {
        var now = DateTime.UtcNow;
        return new WorkflowSagaStateEntity
        {
            CorrelationId = corr,
            TraceId = trace,
            CurrentState = "Completed",
            CurrentNodeId = InnerStartId,
            CurrentAgentKey = string.Empty,
            CurrentRoundId = parentRound,
            RoundCount = 1,
            AgentVersionsJson = "{}",
            DecisionHistoryJson = "[]",
            LogicEvaluationHistoryJson = "[]",
            DecisionCount = 0,
            LogicEvaluationCount = 0,
            WorkflowKey = "inner",
            WorkflowVersion = 1,
            InputsJson = "{}",
            ParentTraceId = parentTrace,
            ParentNodeId = parentNode,
            ParentRoundId = parentRound,
            ParentReviewRound = parentReviewRound,
            ParentReviewMaxRounds = parentReviewMaxRounds,
            SubflowDepth = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Version = 1,
        };
    }

    private static WorkflowSagaStateEntity NewSimpleSaga(Guid corr, Guid trace)
    {
        var now = DateTime.UtcNow;
        return new WorkflowSagaStateEntity
        {
            CorrelationId = corr,
            TraceId = trace,
            CurrentState = "Completed",
            CurrentNodeId = Guid.NewGuid(),
            CurrentAgentKey = "echo",
            CurrentRoundId = Guid.NewGuid(),
            RoundCount = 1,
            AgentVersionsJson = "{}",
            DecisionHistoryJson = "[]",
            LogicEvaluationHistoryJson = "[]",
            DecisionCount = 1,
            LogicEvaluationCount = 0,
            WorkflowKey = "wf",
            WorkflowVersion = 1,
            InputsJson = "{}",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Version = 1,
        };
    }

    private static WorkflowSagaDecisionEntity NewDecision(
        Guid corr, Guid trace, int ordinal, string agentKey, string decision,
        string? outputRef, Guid nodeId, Guid roundId) =>
        new()
        {
            SagaCorrelationId = corr,
            TraceId = trace,
            Ordinal = ordinal,
            AgentKey = agentKey,
            AgentVersion = 1,
            Decision = decision,
            OutputRef = outputRef,
            NodeId = nodeId,
            RoundId = roundId,
            RecordedAtUtc = DateTime.UtcNow,
            OutputPortName = decision,
        };

    private sealed class SimpleRepository : IWorkflowRepository
    {
        private readonly Dictionary<string, Workflow> byKey;

        public SimpleRepository(params Workflow[] workflows)
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
