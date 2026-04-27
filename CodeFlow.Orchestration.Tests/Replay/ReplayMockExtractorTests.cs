using CodeFlow.Orchestration.Replay;
using CodeFlow.Persistence;
using FluentAssertions;

namespace CodeFlow.Orchestration.Tests.Replay;

public sealed class ReplayMockExtractorTests
{
    [Fact]
    public async Task SingleSaga_RealAgentDecisions_QueuedInOrdinalOrderPerAgent()
    {
        var corr = Guid.NewGuid();
        var trace = Guid.NewGuid();
        var saga = NewSaga(corr, trace);

        var store = new InMemoryArtifactStore();
        store.Seed("memory://r1-out", "draft v1");
        store.Seed("memory://r2-out", "draft v2");
        store.Seed("memory://rev1", "ship it");

        var decisions = new[]
        {
            NewDecision(corr, trace, ordinal: 0, agentKey: "producer", decision: "Completed", outputRef: "memory://r1-out"),
            NewDecision(corr, trace, ordinal: 1, agentKey: "reviewer", decision: "Rejected", outputRef: "memory://rev1"),
            NewDecision(corr, trace, ordinal: 2, agentKey: "producer", decision: "Completed", outputRef: "memory://r2-out"),
        };

        var bundle = await ReplayMockExtractor.ExtractAsync(
            saga, new[] { saga }, decisions, store, CancellationToken.None);

        bundle.Mocks.Should().ContainKey("producer").WhoseValue.Should().HaveCount(2);
        bundle.Mocks["producer"][0].Output.Should().Be("draft v1");
        bundle.Mocks["producer"][1].Output.Should().Be("draft v2");
        bundle.Mocks.Should().ContainKey("reviewer").WhoseValue.Should().HaveCount(1);
        bundle.Mocks["reviewer"][0].Decision.Should().Be("Rejected");

        // Per-agent ordinals are 1-based and reset per agent key.
        bundle.Decisions
            .Where(d => d.AgentKey == "producer")
            .Select(d => d.OrdinalPerAgent)
            .Should().Equal(1, 2);
        bundle.Decisions
            .Single(d => d.AgentKey == "reviewer")
            .OrdinalPerAgent.Should().Be(1);
    }

    [Fact]
    public async Task SyntheticSubflowDecisions_AreFilteredAndChildSagaIsRecursivelyWalked()
    {
        // Parent saga has: producer → review-loop synthetic → producer
        // The synthetic decision points at child saga that ran reviewer twice.
        // Expected mock order:
        //   producer queue: parent-r1-output, parent-r2-output (parent's own decisions)
        //   reviewer queue: child-r1, child-r2 (from child saga, walked when we hit synthetic)
        // The synthetic "review-loop:..." entry is dropped from the queue.
        var parentCorr = Guid.NewGuid();
        var parentTrace = Guid.NewGuid();
        var childCorr = Guid.NewGuid();
        var childTrace = Guid.NewGuid();
        var loopNodeId = Guid.NewGuid();
        var loopRoundId = Guid.NewGuid();

        var parentSaga = NewSaga(parentCorr, parentTrace);
        var childSaga = NewSaga(childCorr, childTrace, parentTraceId: parentTrace, parentNodeId: loopNodeId, parentRoundId: loopRoundId);

        var store = new InMemoryArtifactStore();
        store.Seed("memory://parent-r1", "first parent draft");
        store.Seed("memory://child-r1", "first review");
        store.Seed("memory://child-r2", "second review");
        store.Seed("memory://parent-r2", "second parent draft");

        var decisions = new[]
        {
            NewDecision(parentCorr, parentTrace, ordinal: 0, agentKey: "producer", decision: "Completed", outputRef: "memory://parent-r1"),
            NewDecision(parentCorr, parentTrace, ordinal: 1, agentKey: "review-loop:inner", decision: "Approved", outputRef: null, nodeId: loopNodeId, roundId: loopRoundId),
            NewDecision(parentCorr, parentTrace, ordinal: 2, agentKey: "producer", decision: "Completed", outputRef: "memory://parent-r2"),
            NewDecision(childCorr, childTrace, ordinal: 0, agentKey: "reviewer", decision: "Rejected", outputRef: "memory://child-r1"),
            NewDecision(childCorr, childTrace, ordinal: 1, agentKey: "reviewer", decision: "Approved", outputRef: "memory://child-r2"),
        };

        var bundle = await ReplayMockExtractor.ExtractAsync(
            parentSaga, new[] { parentSaga, childSaga }, decisions, store, CancellationToken.None);

        bundle.Mocks.Should().ContainKey("producer").WhoseValue.Should().HaveCount(2);
        bundle.Mocks["producer"][0].Output.Should().Be("first parent draft");
        bundle.Mocks["producer"][1].Output.Should().Be("second parent draft");

        // Reviewer queue threads child decisions in the order the parent encountered the synthetic.
        bundle.Mocks.Should().ContainKey("reviewer").WhoseValue.Should().HaveCount(2);
        bundle.Mocks["reviewer"].Select(m => m.Decision).Should().Equal("Rejected", "Approved");

        // Synthetic decision is filtered — not present as a mock for any agent key starting with
        // "review-loop:" or "subflow:".
        bundle.Mocks.Keys.Should().NotContain(k => k.StartsWith("review-loop:"));
        bundle.Mocks.Keys.Should().NotContain(k => k.StartsWith("subflow:"));

        // Decision refs include the child decisions and assign per-agent ordinals 1,2 within
        // reviewer.
        bundle.Decisions
            .Where(d => d.AgentKey == "reviewer")
            .OrderBy(d => d.OrdinalPerAgent)
            .Select(d => (d.OrdinalPerAgent, d.OriginalDecision))
            .Should().Equal((1, "Rejected"), (2, "Approved"));
    }

    [Theory]
    [InlineData("subflow", true)]
    [InlineData("review-loop", true)]
    [InlineData("subflow:my-key", true)]
    [InlineData("review-loop:reviewer-flow", true)]
    [InlineData("reviewer", false)]
    [InlineData("subflowey", false)]
    [InlineData("", false)]
    public void SyntheticAgentKey_DetectionMatchesSagaStateMachine(string agentKey, bool expected)
    {
        ReplayMockExtractor.IsSyntheticSubflowAgentKey(agentKey).Should().Be(expected);
    }

    private static WorkflowSagaStateEntity NewSaga(
        Guid correlationId,
        Guid traceId,
        Guid? parentTraceId = null,
        Guid? parentNodeId = null,
        Guid? parentRoundId = null)
    {
        var now = DateTime.UtcNow;
        return new WorkflowSagaStateEntity
        {
            CorrelationId = correlationId,
            TraceId = traceId,
            CurrentState = "Completed",
            CurrentNodeId = Guid.NewGuid(),
            CurrentAgentKey = string.Empty,
            CurrentRoundId = Guid.NewGuid(),
            RoundCount = 1,
            AgentVersionsJson = "{}",
            DecisionHistoryJson = "[]",
            LogicEvaluationHistoryJson = "[]",
            DecisionCount = 0,
            LogicEvaluationCount = 0,
            WorkflowKey = "wf",
            WorkflowVersion = 1,
            InputsJson = "{}",
            ParentTraceId = parentTraceId,
            ParentNodeId = parentNodeId,
            ParentRoundId = parentRoundId,
            SubflowDepth = parentTraceId is null ? 0 : 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Version = 1,
        };
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
}
