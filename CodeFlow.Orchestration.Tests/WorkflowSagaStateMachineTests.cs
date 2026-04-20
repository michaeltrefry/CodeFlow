using CodeFlow.Contracts;
using CodeFlow.Orchestration;
using CodeFlow.Persistence;
using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using RuntimeDecisionKind = CodeFlow.Runtime.AgentDecisionKind;
using RuntimeAgentDecision = CodeFlow.Runtime.AgentDecision;

namespace CodeFlow.Orchestration.Tests;

[Collection("Bus integration")]
public sealed class WorkflowSagaStateMachineTests
{
    [Fact]
    public async Task HappyHandoff_ShouldPublishNextAgentInvokeAndRemainRunning()
    {
        var traceId = Guid.NewGuid();
        var roundId = Guid.NewGuid();
        var workflow = BuildWorkflow(
            key: "happy",
            maxRounds: 5,
            edges:
            [
                Edge(from: "evaluator", RuntimeDecisionKind.Completed, to: "reviewer", rotatesRound: false)
            ]);

        var harness = BuildHarness(workflow, new Dictionary<string, int>
        {
            ["reviewer"] = 4
        });

        await harness.Start();
        try
        {
            await harness.Bus.Publish(new AgentInvokeRequested(
                TraceId: traceId,
                RoundId: roundId,
                WorkflowKey: workflow.Key,
                WorkflowVersion: workflow.Version,
                AgentKey: "evaluator",
                AgentVersion: 1,
                InputRef: new Uri("file:///tmp/input.bin")));

            (await harness.Consumed.Any<AgentInvokeRequested>()).Should().BeTrue();

            var sagaHarness = harness.GetSagaStateMachineHarness<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
            var sagaInstance = await sagaHarness.Exists(traceId, x => x.Running);
            sagaInstance.Should().NotBeNull();

            await harness.Bus.Publish(BuildCompletion(
                traceId,
                roundId,
                agentKey: "evaluator",
                agentVersion: 1,
                decision: AgentDecisionKind.Completed));

            var handoffs = harness.Published.Select<AgentInvokeRequested>()
                .Where(x => x.Context.Message.AgentKey == "reviewer")
                .ToList();
            handoffs.Should().HaveCount(1);
            handoffs[0].Context.Message.AgentVersion.Should().Be(4);
            handoffs[0].Context.Message.TraceId.Should().Be(traceId);
            handoffs[0].Context.Message.RoundId.Should().Be(roundId, "non-rotating edge keeps same round");

            var saga = sagaHarness.Sagas.Contains(sagaInstance!.Value);
            saga.Should().NotBeNull();
            saga!.CurrentState.Should().Be(nameof(WorkflowSagaStateMachine.Running));
            saga.CurrentAgentKey.Should().Be("reviewer");
            saga.RoundCount.Should().Be(1);
            saga.GetPinnedVersion("reviewer").Should().Be(4);
            saga.GetDecisionHistory().Should().ContainSingle()
                .Which.Decision.Should().Be(RuntimeDecisionKind.Completed);
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task RotatingEdge_ShouldRotateRoundAndResetCount()
    {
        var traceId = Guid.NewGuid();
        var roundId = Guid.NewGuid();
        var workflow = BuildWorkflow(
            key: "rotate",
            maxRounds: 5,
            edges:
            [
                Edge(from: "evaluator", RuntimeDecisionKind.Completed, to: "reviewer", rotatesRound: true)
            ]);

        var harness = BuildHarness(workflow, new Dictionary<string, int> { ["reviewer"] = 2 });

        await harness.Start();
        try
        {
            await harness.Bus.Publish(new AgentInvokeRequested(
                traceId, roundId, workflow.Key, workflow.Version,
                AgentKey: "evaluator", AgentVersion: 1,
                InputRef: new Uri("file:///tmp/in.bin")));

            var sagaHarness = harness.GetSagaStateMachineHarness<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
            await sagaHarness.Exists(traceId, x => x.Running);

            await harness.Bus.Publish(BuildCompletion(
                traceId, roundId, "evaluator", 1, AgentDecisionKind.Completed));

            var reviewerPublish = harness.Published.Select<AgentInvokeRequested>()
                .Single(x => x.Context.Message.AgentKey == "reviewer");
            reviewerPublish.Context.Message.RoundId.Should().NotBe(roundId, "rotating edge issues a fresh round id");

            var saga = sagaHarness.Sagas.Contains(traceId);
            saga.Should().NotBeNull();
            saga!.RoundCount.Should().Be(0, "rotating edge resets round count");
            saga.CurrentRoundId.Should().NotBe(roundId);
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task NonRotatingEdge_ShouldIncrementRoundCount()
    {
        var traceId = Guid.NewGuid();
        var roundId = Guid.NewGuid();
        var workflow = BuildWorkflow(
            key: "count",
            maxRounds: 10,
            edges:
            [
                Edge("reviewer", RuntimeDecisionKind.Rejected, "evaluator", rotatesRound: false),
                Edge("evaluator", RuntimeDecisionKind.Completed, "reviewer", rotatesRound: false)
            ]);

        var harness = BuildHarness(workflow, new Dictionary<string, int>
        {
            ["evaluator"] = 1,
            ["reviewer"] = 1
        });

        await harness.Start();
        try
        {
            await harness.Bus.Publish(new AgentInvokeRequested(
                traceId, roundId, workflow.Key, workflow.Version,
                "evaluator", 1, new Uri("file:///tmp/in.bin")));

            var sagaHarness = harness.GetSagaStateMachineHarness<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
            await sagaHarness.Exists(traceId, x => x.Running);

            await harness.Bus.Publish(BuildCompletion(
                traceId, roundId, "evaluator", 1, AgentDecisionKind.Completed));

            await sagaHarness.Exists(traceId, s => s.Running);
            SpinWaitUntil(() => sagaHarness.Sagas.Contains(traceId)?.RoundCount == 1);

            await harness.Bus.Publish(BuildCompletion(
                traceId, roundId, "reviewer", 1, AgentDecisionKind.Rejected));

            SpinWaitUntil(() => sagaHarness.Sagas.Contains(traceId)?.RoundCount == 2);

            var saga = sagaHarness.Sagas.Contains(traceId)!;
            saga.RoundCount.Should().Be(2);
            saga.CurrentAgentKey.Should().Be("evaluator");
            saga.GetDecisionHistory().Should().HaveCount(2);
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task UnmappedDecision_ShouldTerminateFailed()
    {
        var traceId = Guid.NewGuid();
        var roundId = Guid.NewGuid();
        var workflow = BuildWorkflow(
            key: "unmapped",
            maxRounds: 5,
            edges: []);

        var harness = BuildHarness(workflow, agentVersions: new Dictionary<string, int>());

        await harness.Start();
        try
        {
            await harness.Bus.Publish(new AgentInvokeRequested(
                traceId, roundId, workflow.Key, workflow.Version,
                "evaluator", 1, new Uri("file:///tmp/in.bin")));

            var sagaHarness = harness.GetSagaStateMachineHarness<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
            await sagaHarness.Exists(traceId, x => x.Running);

            await harness.Bus.Publish(BuildCompletion(
                traceId, roundId, "evaluator", 1, AgentDecisionKind.Rejected));

            await sagaHarness.Exists(traceId, s => s.Failed);
            var saga = sagaHarness.Sagas.Contains(traceId)!;
            saga.CurrentState.Should().Be(nameof(WorkflowSagaStateMachine.Failed));
            saga.PendingTransition.Should().BeNull("state machine clears the pending flag after transitioning");
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task UnmappedCompletedDecision_ShouldTerminateCompleted()
    {
        var traceId = Guid.NewGuid();
        var roundId = Guid.NewGuid();
        var workflow = BuildWorkflow("done", 5, edges: []);

        var harness = BuildHarness(workflow, new Dictionary<string, int>());
        await harness.Start();
        try
        {
            await harness.Bus.Publish(new AgentInvokeRequested(
                traceId, roundId, workflow.Key, workflow.Version,
                "publisher", 1, new Uri("file:///tmp/in.bin")));

            var sagaHarness = harness.GetSagaStateMachineHarness<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
            await sagaHarness.Exists(traceId, s => s.Running);

            await harness.Bus.Publish(BuildCompletion(
                traceId, roundId, "publisher", 1, AgentDecisionKind.Completed));

            await sagaHarness.Exists(traceId, s => s.Completed);
            var saga = sagaHarness.Sagas.Contains(traceId)!;
            saga.CurrentState.Should().Be(nameof(WorkflowSagaStateMachine.Completed));
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task RoundCountExceeded_WithEscalationAgent_ShouldEscalate()
    {
        var traceId = Guid.NewGuid();
        var roundId = Guid.NewGuid();
        var workflow = BuildWorkflow(
            key: "escalate",
            maxRounds: 3,
            escalationAgentKey: "triage",
            edges:
            [
                Edge("looper", RuntimeDecisionKind.Rejected, "looper", rotatesRound: false)
            ]);

        var harness = BuildHarness(workflow, new Dictionary<string, int>
        {
            ["looper"] = 1,
            ["triage"] = 7
        });

        await harness.Start();
        try
        {
            await harness.Bus.Publish(new AgentInvokeRequested(
                traceId, roundId, workflow.Key, workflow.Version,
                "looper", 1, new Uri("file:///tmp/in.bin")));

            var sagaHarness = harness.GetSagaStateMachineHarness<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
            await sagaHarness.Exists(traceId, s => s.Running);

            await harness.Bus.Publish(BuildCompletion(
                traceId, roundId, "looper", 1, AgentDecisionKind.Rejected));
            SpinWaitUntil(() => sagaHarness.Sagas.Contains(traceId)?.RoundCount == 1);

            await harness.Bus.Publish(BuildCompletion(
                traceId, roundId, "looper", 1, AgentDecisionKind.Rejected));
            SpinWaitUntil(() => sagaHarness.Sagas.Contains(traceId)?.RoundCount == 2);

            await harness.Bus.Publish(BuildCompletion(
                traceId, roundId, "looper", 1, AgentDecisionKind.Rejected));

            await sagaHarness.Exists(traceId, s => s.Escalated);
            var saga = sagaHarness.Sagas.Contains(traceId)!;
            saga.CurrentState.Should().Be(nameof(WorkflowSagaStateMachine.Escalated));
            saga.GetPinnedVersion("triage").Should().Be(7);

            var triagePublish = harness.Published.Select<AgentInvokeRequested>()
                .SingleOrDefault(x => x.Context.Message.AgentKey == "triage");
            triagePublish.Should().NotBeNull("escalation agent invocation was published");
            triagePublish!.Context.Message.AgentVersion.Should().Be(7);
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task RoundCountExceeded_WithoutEscalationAgent_ShouldFail()
    {
        var traceId = Guid.NewGuid();
        var roundId = Guid.NewGuid();
        var workflow = BuildWorkflow(
            key: "capfail",
            maxRounds: 3,
            edges:
            [
                Edge("looper", RuntimeDecisionKind.Rejected, "looper", rotatesRound: false)
            ]);

        var harness = BuildHarness(workflow, new Dictionary<string, int> { ["looper"] = 1 });
        await harness.Start();
        try
        {
            await harness.Bus.Publish(new AgentInvokeRequested(
                traceId, roundId, workflow.Key, workflow.Version,
                "looper", 1, new Uri("file:///tmp/in.bin")));

            var sagaHarness = harness.GetSagaStateMachineHarness<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
            await sagaHarness.Exists(traceId, s => s.Running);

            await harness.Bus.Publish(BuildCompletion(traceId, roundId, "looper", 1, AgentDecisionKind.Rejected));
            SpinWaitUntil(() => sagaHarness.Sagas.Contains(traceId)?.RoundCount == 1);
            await harness.Bus.Publish(BuildCompletion(traceId, roundId, "looper", 1, AgentDecisionKind.Rejected));
            SpinWaitUntil(() => sagaHarness.Sagas.Contains(traceId)?.RoundCount == 2);
            await harness.Bus.Publish(BuildCompletion(traceId, roundId, "looper", 1, AgentDecisionKind.Rejected));

            await sagaHarness.Exists(traceId, s => s.Failed);
        }
        finally
        {
            await harness.Stop();
        }
    }

    private static ITestHarness BuildHarness(
        Workflow workflow,
        IReadOnlyDictionary<string, int> agentVersions)
    {
        var provider = new ServiceCollection()
            .AddSingleton<IWorkflowRepository>(new FakeWorkflowRepository(workflow))
            .AddSingleton<IAgentConfigRepository>(new FakeAgentConfigRepository(agentVersions))
            .AddMassTransitTestHarness(x =>
            {
                x.AddSagaStateMachine<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
            })
            .BuildServiceProvider(true);

        return provider.GetRequiredService<ITestHarness>();
    }

    private static Workflow BuildWorkflow(
        string key,
        int maxRounds,
        IReadOnlyList<WorkflowEdge> edges,
        string? escalationAgentKey = null)
    {
        return new Workflow(
            Key: key,
            Version: 1,
            Name: key,
            StartAgentKey: edges.FirstOrDefault()?.FromAgentKey ?? "start",
            EscalationAgentKey: escalationAgentKey,
            MaxRoundsPerRound: maxRounds,
            CreatedAtUtc: DateTime.UtcNow,
            Edges: edges);
    }

    private static WorkflowEdge Edge(
        string from,
        RuntimeDecisionKind decision,
        string to,
        bool rotatesRound,
        int sortOrder = 0)
    {
        return new WorkflowEdge(from, decision, Discriminator: null, to, rotatesRound, sortOrder);
    }

    private static AgentInvocationCompleted BuildCompletion(
        Guid traceId,
        Guid roundId,
        string agentKey,
        int agentVersion,
        Contracts.AgentDecisionKind decision)
    {
        return new AgentInvocationCompleted(
            TraceId: traceId,
            RoundId: roundId,
            AgentKey: agentKey,
            AgentVersion: agentVersion,
            OutputRef: new Uri($"file:///tmp/{agentKey}-out.bin"),
            Decision: decision,
            DecisionPayload: JsonDocument.Parse($"{{\"kind\":\"{decision}\"}}").RootElement,
            Duration: TimeSpan.FromMilliseconds(1),
            TokenUsage: new Contracts.TokenUsage(0, 0, 0));
    }

    private static void SpinWaitUntil(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }

            Thread.Sleep(25);
        }

        throw new TimeoutException("Condition not reached in 5 seconds.");
    }

    private sealed class FakeWorkflowRepository(Workflow workflow) : IWorkflowRepository
    {
        public Task<Workflow> GetAsync(string key, int version, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(workflow);
        }

        public Task<Workflow?> GetLatestAsync(string key, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<Workflow?>(workflow);
        }

        public Task<IReadOnlyList<Workflow>> ListLatestAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<Workflow>>(new[] { workflow });
        }

        public Task<IReadOnlyList<Workflow>> ListVersionsAsync(string key, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<Workflow>>(new[] { workflow });
        }

        public Task<WorkflowEdge?> FindNextAsync(
            string key,
            int version,
            string fromAgentKey,
            RuntimeAgentDecision decision,
            JsonElement? discriminator = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(workflow.FindNext(fromAgentKey, decision, discriminator));
        }

        public Task<int> CreateNewVersionAsync(WorkflowDraft draft, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FakeAgentConfigRepository(IReadOnlyDictionary<string, int> versions)
        : IAgentConfigRepository
    {
        public Task<AgentConfig> GetAsync(string key, int version, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<int> CreateNewVersionAsync(string key, string configJson, string? createdBy, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<int> GetLatestVersionAsync(string key, CancellationToken cancellationToken = default)
        {
            if (versions.TryGetValue(key, out var version))
            {
                return Task.FromResult(version);
            }

            throw new AgentConfigNotFoundException(key, 0);
        }
    }
}
