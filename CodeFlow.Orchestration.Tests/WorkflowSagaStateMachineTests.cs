using CodeFlow.Contracts;
using CodeFlow.Orchestration.Scripting;
using CodeFlow.Persistence;
using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using System.Text;
using System.Text.Json;
using RuntimeDecisionKind = CodeFlow.Runtime.AgentDecisionKind;

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
            startAgentKey: "evaluator",
            edges:
            [
                Edge("evaluator", RuntimeDecisionKind.Completed, "reviewer", rotatesRound: false)
            ]);

        var harness = BuildHarness(workflow, new Dictionary<string, int> { ["reviewer"] = 4 });

        await harness.Start();
        try
        {
            await PublishStart(harness, workflow, traceId, roundId);

            (await harness.Consumed.Any<AgentInvokeRequested>()).Should().BeTrue();

            var sagaHarness = harness.GetSagaStateMachineHarness<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
            var sagaInstance = await sagaHarness.Exists(traceId, x => x.Running);
            sagaInstance.Should().NotBeNull();

            await harness.Bus.Publish(BuildCompletion(workflow, traceId, roundId, "evaluator", 1, AgentDecisionKind.Completed));

            var handoffs = harness.Published.Select<AgentInvokeRequested>()
                .Where(x => x.Context.Message.AgentKey == "reviewer")
                .ToList();
            handoffs.Should().HaveCount(1);
            handoffs[0].Context.Message.AgentVersion.Should().Be(4);
            handoffs[0].Context.Message.TraceId.Should().Be(traceId);
            handoffs[0].Context.Message.RoundId.Should().Be(roundId, "non-rotating edge keeps same round");
            handoffs[0].Context.Message.NodeId.Should().Be(NodeIdFor(workflow, "reviewer"));

            var saga = sagaHarness.Sagas.Contains(sagaInstance!.Value);
            saga.Should().NotBeNull();
            saga!.CurrentState.Should().Be(nameof(WorkflowSagaStateMachine.Running));
            saga.CurrentAgentKey.Should().Be("reviewer");
            saga.CurrentNodeId.Should().Be(NodeIdFor(workflow, "reviewer"));
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
            startAgentKey: "evaluator",
            edges:
            [
                Edge("evaluator", RuntimeDecisionKind.Completed, "reviewer", rotatesRound: true)
            ]);

        var harness = BuildHarness(workflow, new Dictionary<string, int> { ["reviewer"] = 2 });

        await harness.Start();
        try
        {
            await PublishStart(harness, workflow, traceId, roundId);

            var sagaHarness = harness.GetSagaStateMachineHarness<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
            await sagaHarness.Exists(traceId, x => x.Running);

            await harness.Bus.Publish(BuildCompletion(workflow, traceId, roundId, "evaluator", 1, AgentDecisionKind.Completed));

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
            startAgentKey: "evaluator",
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
            await PublishStart(harness, workflow, traceId, roundId);

            var sagaHarness = harness.GetSagaStateMachineHarness<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
            await sagaHarness.Exists(traceId, x => x.Running);

            await harness.Bus.Publish(BuildCompletion(workflow, traceId, roundId, "evaluator", 1, AgentDecisionKind.Completed));

            await sagaHarness.Exists(traceId, s => s.Running);
            SpinWaitUntil(() => sagaHarness.Sagas.Contains(traceId)?.RoundCount == 1);

            await harness.Bus.Publish(BuildCompletion(workflow, traceId, roundId, "reviewer", 1, AgentDecisionKind.Rejected));

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
    public async Task StaleRoundCompletion_ShouldBeIgnored()
    {
        var traceId = Guid.NewGuid();
        var currentRoundId = Guid.NewGuid();
        var staleRoundId = Guid.NewGuid();

        var workflow = BuildWorkflow(
            key: "stale-round",
            maxRounds: 10,
            startAgentKey: "evaluator",
            edges:
            [
                Edge("evaluator", RuntimeDecisionKind.Completed, "reviewer", rotatesRound: false)
            ]);

        var harness = BuildHarness(workflow, new Dictionary<string, int> { ["reviewer"] = 1 });

        await harness.Start();
        try
        {
            await PublishStart(harness, workflow, traceId, currentRoundId);

            var sagaHarness = harness.GetSagaStateMachineHarness<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
            await sagaHarness.Exists(traceId, x => x.Running);

            // Publish a completion whose RoundId does not match the saga's current round
            // (simulates a delayed redelivery or duplicate completion from a prior round).
            await harness.Bus.Publish(BuildCompletion(
                workflow, traceId, staleRoundId, "evaluator", 1, AgentDecisionKind.Completed));

            // Give the saga a moment to process (or reject) the message.
            await Task.Delay(200);

            var saga = sagaHarness.Sagas.Contains(traceId)!;
            saga.CurrentAgentKey.Should().Be("evaluator",
                "stale-round completion must not advance the saga to the next agent");
            saga.GetDecisionHistory().Should().BeEmpty(
                "stale-round completion must not be recorded in decision history");

            // Sanity: a completion with the correct RoundId still advances the saga.
            await harness.Bus.Publish(BuildCompletion(
                workflow, traceId, currentRoundId, "evaluator", 1, AgentDecisionKind.Completed));
            SpinWaitUntil(() => sagaHarness.Sagas.Contains(traceId)?.CurrentAgentKey == "reviewer");
            sagaHarness.Sagas.Contains(traceId)!.GetDecisionHistory().Should().ContainSingle();
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
            startAgentKey: "evaluator",
            edges: []);

        var harness = BuildHarness(workflow, agentVersions: new Dictionary<string, int>());

        await harness.Start();
        try
        {
            await PublishStart(harness, workflow, traceId, roundId);

            var sagaHarness = harness.GetSagaStateMachineHarness<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
            await sagaHarness.Exists(traceId, x => x.Running);

            await harness.Bus.Publish(BuildCompletion(workflow, traceId, roundId, "evaluator", 1, AgentDecisionKind.Rejected));

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
        var workflow = BuildWorkflow("done", 5, startAgentKey: "publisher", edges: []);

        var harness = BuildHarness(workflow, new Dictionary<string, int>());
        await harness.Start();
        try
        {
            await PublishStart(harness, workflow, traceId, roundId);

            var sagaHarness = harness.GetSagaStateMachineHarness<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
            await sagaHarness.Exists(traceId, s => s.Running);

            await harness.Bus.Publish(BuildCompletion(workflow, traceId, roundId, "publisher", 1, AgentDecisionKind.Completed));

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
    public async Task RoundCountExceeded_WithEscalationAgent_ShouldDispatchAndStayRunning()
    {
        var (traceId, _, sagaHarness, harness, workflow) = await RunUntilEscalationDispatchedAsync();
        try
        {
            var saga = sagaHarness.Sagas.Contains(traceId)!;
            saga.CurrentState.Should().Be(nameof(WorkflowSagaStateMachine.Running),
                "saga stays running until the escalation agent completes");
            saga.EscalatedFromNodeId.Should().Be(NodeIdFor(workflow, "looper"));
            saga.CurrentAgentKey.Should().Be("triage");
            saga.CurrentNodeId.Should().Be(NodeIdFor(workflow, "triage"));
            saga.GetPinnedVersion("triage").Should().Be(7);

            var triagePublish = harness.Published.Select<AgentInvokeRequested>()
                .SingleOrDefault(x => x.Context.Message.AgentKey == "triage");
            triagePublish.Should().NotBeNull();
            triagePublish!.Context.Message.AgentVersion.Should().Be(7);
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task EscalationAgent_Approved_ShouldRecoverByResumingOverflowedAgent()
    {
        var (traceId, roundId, sagaHarness, harness, workflow) = await RunUntilEscalationDispatchedAsync();
        try
        {
            var escalationRoundId = sagaHarness.Sagas.Contains(traceId)!.CurrentRoundId;

            await harness.Bus.Publish(BuildCompletion(workflow, traceId, escalationRoundId, "triage", 7, AgentDecisionKind.Approved));

            SpinWaitUntil(() => harness.Published.Select<AgentInvokeRequested>()
                .Any(x => x.Context.Message.AgentKey == "looper" && x.Context.Message.RoundId != roundId));

            var recoveryPublishes = harness.Published.Select<AgentInvokeRequested>()
                .Where(x => x.Context.Message.AgentKey == "looper" && x.Context.Message.RoundId != roundId)
                .ToList();
            recoveryPublishes.Should().ContainSingle("recovery re-invokes the overflowed agent once");
            recoveryPublishes[0].Context.Message.RoundId.Should().NotBe(escalationRoundId,
                "recovery starts a fresh round so the cap resets");

            var saga = sagaHarness.Sagas.Contains(traceId)!;
            saga.CurrentState.Should().Be(nameof(WorkflowSagaStateMachine.Running));
            saga.EscalatedFromNodeId.Should().BeNull("escalation flag is cleared on recovery");
            saga.CurrentAgentKey.Should().Be("looper");
            saga.RoundCount.Should().Be(0);
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task EscalationAgent_Completed_ShouldTerminateAsCompleted()
    {
        var (traceId, _, sagaHarness, harness, workflow) = await RunUntilEscalationDispatchedAsync();
        try
        {
            var escalationRoundId = sagaHarness.Sagas.Contains(traceId)!.CurrentRoundId;

            await harness.Bus.Publish(BuildCompletion(workflow, traceId, escalationRoundId, "triage", 7, AgentDecisionKind.Completed));

            await sagaHarness.Exists(traceId, s => s.Completed);
            var saga = sagaHarness.Sagas.Contains(traceId)!;
            saga.EscalatedFromNodeId.Should().BeNull();
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task EscalationAgent_Rejected_ShouldTerminateAsEscalated()
    {
        var (traceId, _, sagaHarness, harness, workflow) = await RunUntilEscalationDispatchedAsync();
        try
        {
            var escalationRoundId = sagaHarness.Sagas.Contains(traceId)!.CurrentRoundId;

            await harness.Bus.Publish(BuildCompletion(workflow, traceId, escalationRoundId, "triage", 7, AgentDecisionKind.Rejected));

            await sagaHarness.Exists(traceId, s => s.Escalated);
            var saga = sagaHarness.Sagas.Contains(traceId)!;
            saga.EscalatedFromNodeId.Should().BeNull();
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task EscalationAgent_Failed_ShouldTerminateAsFailed()
    {
        var (traceId, _, sagaHarness, harness, workflow) = await RunUntilEscalationDispatchedAsync();
        try
        {
            var escalationRoundId = sagaHarness.Sagas.Contains(traceId)!.CurrentRoundId;

            await harness.Bus.Publish(BuildCompletion(workflow, traceId, escalationRoundId, "triage", 7, AgentDecisionKind.Failed));

            await sagaHarness.Exists(traceId, s => s.Failed);
            var saga = sagaHarness.Sagas.Contains(traceId)!;
            saga.EscalatedFromNodeId.Should().BeNull();
        }
        finally
        {
            await harness.Stop();
        }
    }

    private static async Task<(Guid TraceId, Guid RoundId,
        ISagaStateMachineTestHarness<WorkflowSagaStateMachine, WorkflowSagaStateEntity> SagaHarness,
        ITestHarness Harness,
        Workflow Workflow)> RunUntilEscalationDispatchedAsync()
    {
        var traceId = Guid.NewGuid();
        var roundId = Guid.NewGuid();
        var workflow = BuildWorkflow(
            key: "escalate",
            maxRounds: 3,
            startAgentKey: "looper",
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

        await PublishStart(harness, workflow, traceId, roundId);

        var sagaHarness = harness.GetSagaStateMachineHarness<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
        await sagaHarness.Exists(traceId, s => s.Running);

        await harness.Bus.Publish(BuildCompletion(workflow, traceId, roundId, "looper", 1, AgentDecisionKind.Rejected));
        SpinWaitUntil(() => sagaHarness.Sagas.Contains(traceId)?.RoundCount == 1);

        await harness.Bus.Publish(BuildCompletion(workflow, traceId, roundId, "looper", 1, AgentDecisionKind.Rejected));
        SpinWaitUntil(() => sagaHarness.Sagas.Contains(traceId)?.RoundCount == 2);

        await harness.Bus.Publish(BuildCompletion(workflow, traceId, roundId, "looper", 1, AgentDecisionKind.Rejected));
        SpinWaitUntil(() => sagaHarness.Sagas.Contains(traceId)?.EscalatedFromNodeId == NodeIdFor(workflow, "looper"));

        return (traceId, roundId, sagaHarness, harness, workflow);
    }

    [Fact]
    public async Task RoundCountExceeded_WithoutEscalationAgent_ShouldFail()
    {
        var traceId = Guid.NewGuid();
        var roundId = Guid.NewGuid();
        var workflow = BuildWorkflow(
            key: "capfail",
            maxRounds: 3,
            startAgentKey: "looper",
            edges:
            [
                Edge("looper", RuntimeDecisionKind.Rejected, "looper", rotatesRound: false)
            ]);

        var harness = BuildHarness(workflow, new Dictionary<string, int> { ["looper"] = 1 });
        await harness.Start();
        try
        {
            await PublishStart(harness, workflow, traceId, roundId);

            var sagaHarness = harness.GetSagaStateMachineHarness<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
            await sagaHarness.Exists(traceId, s => s.Running);

            await harness.Bus.Publish(BuildCompletion(workflow, traceId, roundId, "looper", 1, AgentDecisionKind.Rejected));
            SpinWaitUntil(() => sagaHarness.Sagas.Contains(traceId)?.RoundCount == 1);
            await harness.Bus.Publish(BuildCompletion(workflow, traceId, roundId, "looper", 1, AgentDecisionKind.Rejected));
            SpinWaitUntil(() => sagaHarness.Sagas.Contains(traceId)?.RoundCount == 2);
            await harness.Bus.Publish(BuildCompletion(workflow, traceId, roundId, "looper", 1, AgentDecisionKind.Rejected));

            await sagaHarness.Exists(traceId, s => s.Failed);
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task LogicNode_RoutesOnScriptChosenPort()
    {
        var traceId = Guid.NewGuid();
        var roundId = Guid.NewGuid();
        var logicNodeId = Guid.NewGuid();
        const string script = """
            if (input.kind === 'NewProject') { setNodePath('NewProjectFlow'); }
            else if (input.kind === 'feature') { setNodePath('FeatureFlow'); }
            else { setNodePath('BugFixFlow'); }
            """;

        var workflow = BuildWorkflowWithLogic(
            key: "logic",
            startAgentKey: "classifier",
            logicNodeId: logicNodeId,
            logicScript: script,
            logicOutputPorts: new[] { "NewProjectFlow", "FeatureFlow", "BugFixFlow" },
            downstreamAgents: new[] { "newProjectFlow", "featureFlow", "bugFixFlow" },
            classifierToLogicPort: "Completed",
            logicPortToAgent: new Dictionary<string, string>
            {
                ["NewProjectFlow"] = "newProjectFlow",
                ["FeatureFlow"] = "featureFlow",
                ["BugFixFlow"] = "bugFixFlow"
            });

        var outputRef = new Uri("file:///tmp/classifier-out.bin");
        var artifactStore = new StubArtifactStore(defaultJson: """{"kind":"feature","summary":"add toggle"}""");
        var harness = BuildHarness(workflow, new Dictionary<string, int>
        {
            ["newProjectFlow"] = 1,
            ["featureFlow"] = 1,
            ["bugFixFlow"] = 1
        }, artifactStore);

        await harness.Start();
        try
        {
            await PublishStart(harness, workflow, traceId, roundId);

            var sagaHarness = harness.GetSagaStateMachineHarness<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
            await sagaHarness.Exists(traceId, s => s.Running);

            await harness.Bus.Publish(new AgentInvocationCompleted(
                TraceId: traceId,
                RoundId: roundId,
                FromNodeId: NodeIdFor(workflow, "classifier"),
                AgentKey: "classifier",
                AgentVersion: 1,
                OutputPortName: "Completed",
                OutputRef: outputRef,
                Decision: AgentDecisionKind.Completed,
                DecisionPayload: JsonDocument.Parse("""{"kind":"Completed"}""").RootElement,
                Duration: TimeSpan.FromMilliseconds(1),
                TokenUsage: new Contracts.TokenUsage(0, 0, 0)));

            SpinWaitUntil(() => harness.Published.Select<AgentInvokeRequested>()
                .Any(x => x.Context.Message.AgentKey == "featureFlow"));

            var dispatches = harness.Published.Select<AgentInvokeRequested>()
                .Where(x => x.Context.Message.AgentKey != "classifier")
                .ToList();

            dispatches.Should().ContainSingle()
                .Which.Context.Message.AgentKey.Should().Be("featureFlow",
                    "the logic script routed on input.kind === 'feature'");

            var saga = sagaHarness.Sagas.Contains(traceId)!;
            saga.CurrentAgentKey.Should().Be("featureFlow");
            saga.CurrentNodeId.Should().Be(NodeIdFor(workflow, "featureFlow"));

            var logicHistory = saga.GetLogicEvaluationHistory();
            logicHistory.Should().ContainSingle()
                .Which.OutputPortName.Should().Be("FeatureFlow");
            logicHistory[0].FailureKind.Should().BeNull();
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task LogicNode_ScriptFailure_RoutesViaFailedPort()
    {
        var traceId = Guid.NewGuid();
        var roundId = Guid.NewGuid();
        var logicNodeId = Guid.NewGuid();
        const string script = "throw new Error('script exploded');";

        var workflow = BuildWorkflowWithLogic(
            key: "logic-failed",
            startAgentKey: "classifier",
            logicNodeId: logicNodeId,
            logicScript: script,
            logicOutputPorts: new[] { AgentDecisionPorts.FailedPort },
            downstreamAgents: new[] { "fallback" },
            classifierToLogicPort: "Completed",
            logicPortToAgent: new Dictionary<string, string>
            {
                [AgentDecisionPorts.FailedPort] = "fallback"
            });

        var artifactStore = new StubArtifactStore(defaultJson: "{}");
        var harness = BuildHarness(workflow, new Dictionary<string, int> { ["fallback"] = 1 }, artifactStore);

        await harness.Start();
        try
        {
            await PublishStart(harness, workflow, traceId, roundId);

            var sagaHarness = harness.GetSagaStateMachineHarness<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
            await sagaHarness.Exists(traceId, s => s.Running);

            await harness.Bus.Publish(new AgentInvocationCompleted(
                TraceId: traceId,
                RoundId: roundId,
                FromNodeId: NodeIdFor(workflow, "classifier"),
                AgentKey: "classifier",
                AgentVersion: 1,
                OutputPortName: "Completed",
                OutputRef: new Uri("file:///tmp/out.bin"),
                Decision: AgentDecisionKind.Completed,
                DecisionPayload: JsonDocument.Parse("""{"kind":"Completed"}""").RootElement,
                Duration: TimeSpan.FromMilliseconds(1),
                TokenUsage: new Contracts.TokenUsage(0, 0, 0)));

            SpinWaitUntil(() => harness.Published.Select<AgentInvokeRequested>()
                .Any(x => x.Context.Message.AgentKey == "fallback"));

            var saga = sagaHarness.Sagas.Contains(traceId)!;
            saga.CurrentAgentKey.Should().Be("fallback");

            var logicHistory = saga.GetLogicEvaluationHistory();
            logicHistory.Should().ContainSingle();
            logicHistory[0].FailureKind.Should().Be("ScriptError");
            logicHistory[0].FailureMessage.Should().Contain("script exploded");
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task LogicNode_SetContext_MergesIntoSagaInputsAndDownstreamDispatch()
    {
        var traceId = Guid.NewGuid();
        var roundId = Guid.NewGuid();
        var logicNodeId = Guid.NewGuid();
        // Logic node appends the HITL answer to a running transcript and keeps a turn counter.
        const string script = """
            var prior = (context.transcript || []).slice();
            prior.push({ q: input.question, a: input.answer });
            setContext('transcript', prior);
            setContext('turn', (context.turn || 0) + 1);
            setNodePath('NextTurn');
            """;

        var workflow = BuildWorkflowWithLogic(
            key: "logic-setContext",
            startAgentKey: "interviewer",
            logicNodeId: logicNodeId,
            logicScript: script,
            logicOutputPorts: new[] { "NextTurn" },
            downstreamAgents: new[] { "interviewerRoundTwo" },
            classifierToLogicPort: "Completed",
            logicPortToAgent: new Dictionary<string, string>
            {
                ["NextTurn"] = "interviewerRoundTwo"
            });

        var artifactStore = new StubArtifactStore(
            defaultJson: """{"question":"what language?","answer":"Go"}""");
        var harness = BuildHarness(workflow, new Dictionary<string, int>
        {
            ["interviewerRoundTwo"] = 1
        }, artifactStore);

        await harness.Start();
        try
        {
            // Seed workflow with a prior transcript entry so we exercise read-modify-write.
            await harness.Bus.Publish(new AgentInvokeRequested(
                TraceId: traceId,
                RoundId: roundId,
                WorkflowKey: workflow.Key,
                WorkflowVersion: workflow.Version,
                NodeId: workflow.StartNode.Id,
                AgentKey: workflow.StartNode.AgentKey!,
                AgentVersion: 1,
                InputRef: new Uri("file:///tmp/in.bin"),
                ContextInputs: new Dictionary<string, JsonElement>
                {
                    ["transcript"] = JsonDocument.Parse("""[{"q":"hello?","a":"hi"}]""").RootElement.Clone()
                }));

            var sagaHarness = harness.GetSagaStateMachineHarness<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
            await sagaHarness.Exists(traceId, s => s.Running);

            await harness.Bus.Publish(new AgentInvocationCompleted(
                TraceId: traceId,
                RoundId: roundId,
                FromNodeId: NodeIdFor(workflow, "interviewer"),
                AgentKey: "interviewer",
                AgentVersion: 1,
                OutputPortName: "Completed",
                OutputRef: new Uri("file:///tmp/interviewer-out.bin"),
                Decision: AgentDecisionKind.Completed,
                DecisionPayload: JsonDocument.Parse("""{"kind":"Completed"}""").RootElement,
                Duration: TimeSpan.FromMilliseconds(1),
                TokenUsage: new Contracts.TokenUsage(0, 0, 0)));

            SpinWaitUntil(() => harness.Published.Select<AgentInvokeRequested>()
                .Any(x => x.Context.Message.AgentKey == "interviewerRoundTwo"));

            var downstreamDispatch = harness.Published.Select<AgentInvokeRequested>()
                .Single(x => x.Context.Message.AgentKey == "interviewerRoundTwo");

            var contextInputs = downstreamDispatch.Context.Message.ContextInputs;
            contextInputs.Should().ContainKey("transcript");
            contextInputs.Should().ContainKey("turn");

            var transcript = contextInputs["transcript"];
            transcript.ValueKind.Should().Be(JsonValueKind.Array);
            transcript.GetArrayLength().Should().Be(2, "prior entry + one appended by the logic node");
            transcript[1].GetProperty("q").GetString().Should().Be("what language?");
            transcript[1].GetProperty("a").GetString().Should().Be("Go");

            contextInputs["turn"].GetInt32().Should().Be(1);

            var saga = sagaHarness.Sagas.Contains(traceId)!;
            saga.InputsJson.Should().Contain("\"transcript\"")
                .And.Contain("\"turn\":1");
        }
        finally
        {
            await harness.Stop();
        }
    }

    private static Workflow BuildWorkflowWithLogic(
        string key,
        string startAgentKey,
        Guid logicNodeId,
        string logicScript,
        IReadOnlyList<string> logicOutputPorts,
        IReadOnlyList<string> downstreamAgents,
        string classifierToLogicPort,
        IReadOnlyDictionary<string, string> logicPortToAgent)
    {
        var nodes = new List<WorkflowNode>();
        var nodeIds = new Dictionary<string, Guid>(StringComparer.Ordinal);

        var startId = Guid.NewGuid();
        nodeIds[startAgentKey] = startId;
        nodes.Add(new WorkflowNode(startId, WorkflowNodeKind.Start, startAgentKey, 1, null, AllDecisionPorts, 0, 0));

        nodes.Add(new WorkflowNode(logicNodeId, WorkflowNodeKind.Logic, null, null, logicScript, logicOutputPorts, 250, 0));

        foreach (var agentKey in downstreamAgents)
        {
            var id = Guid.NewGuid();
            nodeIds[agentKey] = id;
            nodes.Add(new WorkflowNode(id, WorkflowNodeKind.Agent, agentKey, 1, null, AllDecisionPorts, 500, 0));
        }

        var edges = new List<WorkflowEdge>
        {
            new(startId, classifierToLogicPort, logicNodeId, WorkflowEdge.DefaultInputPort, false, 0)
        };

        var sortOrder = 1;
        foreach (var (port, agent) in logicPortToAgent)
        {
            edges.Add(new WorkflowEdge(logicNodeId, port, nodeIds[agent], WorkflowEdge.DefaultInputPort, false, sortOrder++));
        }

        return new Workflow(
            Key: key,
            Version: 1,
            Name: key,
            MaxRoundsPerRound: 5,
            CreatedAtUtc: DateTime.UtcNow,
            Nodes: nodes,
            Edges: edges,
            Inputs: Array.Empty<WorkflowInput>());
    }

    [Fact]
    public async Task ScriptedAgent_RoutesViaScriptChosenPort()
    {
        // The scripted Start agent inspects its output and picks a custom port
        // that is unrelated to the AgentDecisionKind name.
        var traceId = Guid.NewGuid();
        var roundId = Guid.NewGuid();
        const string script = """
            setNodePath(input.verdict === 'ok' ? 'Accept' : 'Reject');
            """;
        var workflow = BuildWorkflowWithScriptedSource(
            key: "scripted-agent",
            sourceAgentKey: "classifier",
            sourceScript: script,
            sourceOutputPorts: new[] { "Accept", "Reject" },
            downstream: new Dictionary<string, string>
            {
                ["Accept"] = "accepter",
                ["Reject"] = "rejecter"
            });

        var artifactStore = new StubArtifactStore(defaultJson: """{"verdict":"ok"}""");
        var harness = BuildHarness(workflow, new Dictionary<string, int>
        {
            ["accepter"] = 1,
            ["rejecter"] = 1
        }, artifactStore);
        await harness.Start();
        try
        {
            await PublishStart(harness, workflow, traceId, roundId);
            var sagaHarness = harness.GetSagaStateMachineHarness<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
            await sagaHarness.Exists(traceId, s => s.Running);

            await harness.Bus.Publish(new AgentInvocationCompleted(
                TraceId: traceId,
                RoundId: roundId,
                FromNodeId: NodeIdFor(workflow, "classifier"),
                AgentKey: "classifier",
                AgentVersion: 1,
                OutputPortName: "Completed",
                OutputRef: new Uri("file:///tmp/classifier-out.bin"),
                Decision: AgentDecisionKind.Completed,
                DecisionPayload: JsonDocument.Parse("""{"kind":"Completed"}""").RootElement,
                Duration: TimeSpan.FromMilliseconds(1),
                TokenUsage: new Contracts.TokenUsage(0, 0, 0)));

            SpinWaitUntil(() => harness.Published.Select<AgentInvokeRequested>()
                .Any(x => x.Context.Message.AgentKey == "accepter"));

            var saga = sagaHarness.Sagas.Contains(traceId)!;
            saga.CurrentAgentKey.Should().Be("accepter");

            var logicHistory = saga.GetLogicEvaluationHistory();
            logicHistory.Should().ContainSingle()
                .Which.NodeId.Should().Be(NodeIdFor(workflow, "classifier"));
            logicHistory[0].OutputPortName.Should().Be("Accept");
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task ScriptedAgent_ScriptThrows_FallsBackToDecisionKindPort()
    {
        var traceId = Guid.NewGuid();
        var roundId = Guid.NewGuid();
        const string script = "throw new Error('unreachable for some reason');";
        var workflow = BuildWorkflowWithScriptedSource(
            key: "scripted-fallback",
            sourceAgentKey: "classifier",
            sourceScript: script,
            // Include both custom and AgentDecisionKind ports so the fallback resolves.
            sourceOutputPorts: new[] { "Accept", "Reject", "Completed" },
            downstream: new Dictionary<string, string>
            {
                ["Completed"] = "fallbackHandler"
            });

        var artifactStore = new StubArtifactStore(defaultJson: "{}");
        var harness = BuildHarness(workflow, new Dictionary<string, int>
        {
            ["fallbackHandler"] = 1
        }, artifactStore);
        await harness.Start();
        try
        {
            await PublishStart(harness, workflow, traceId, roundId);
            var sagaHarness = harness.GetSagaStateMachineHarness<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
            await sagaHarness.Exists(traceId, s => s.Running);

            await harness.Bus.Publish(new AgentInvocationCompleted(
                TraceId: traceId,
                RoundId: roundId,
                FromNodeId: NodeIdFor(workflow, "classifier"),
                AgentKey: "classifier",
                AgentVersion: 1,
                OutputPortName: "Completed",
                OutputRef: new Uri("file:///tmp/classifier-out.bin"),
                Decision: AgentDecisionKind.Completed,
                DecisionPayload: JsonDocument.Parse("""{"kind":"Completed"}""").RootElement,
                Duration: TimeSpan.FromMilliseconds(1),
                TokenUsage: new Contracts.TokenUsage(0, 0, 0)));

            SpinWaitUntil(() => harness.Published.Select<AgentInvokeRequested>()
                .Any(x => x.Context.Message.AgentKey == "fallbackHandler"));

            var saga = sagaHarness.Sagas.Contains(traceId)!;
            saga.CurrentAgentKey.Should().Be("fallbackHandler");

            var logicHistory = saga.GetLogicEvaluationHistory();
            logicHistory.Should().ContainSingle();
            logicHistory[0].FailureKind.Should().Be("ScriptError");
            logicHistory[0].FailureMessage.Should().Contain("unreachable");
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task ScriptedAgent_ReadsInputDecision_AndBranches()
    {
        var traceId = Guid.NewGuid();
        var roundId = Guid.NewGuid();
        const string script = """
            setNodePath(input.decision === 'Rejected' ? 'Revise' : 'Accept');
            """;
        var workflow = BuildWorkflowWithScriptedSource(
            key: "scripted-decision-branch",
            sourceAgentKey: "reviewer",
            sourceScript: script,
            sourceOutputPorts: new[] { "Accept", "Revise" },
            downstream: new Dictionary<string, string>
            {
                ["Accept"] = "publisher",
                ["Revise"] = "editor"
            });

        var artifactStore = new StubArtifactStore(defaultJson: "{}");
        var harness = BuildHarness(workflow, new Dictionary<string, int>
        {
            ["publisher"] = 1,
            ["editor"] = 1
        }, artifactStore);
        await harness.Start();
        try
        {
            await PublishStart(harness, workflow, traceId, roundId);
            var sagaHarness = harness.GetSagaStateMachineHarness<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
            await sagaHarness.Exists(traceId, s => s.Running);

            await harness.Bus.Publish(new AgentInvocationCompleted(
                TraceId: traceId,
                RoundId: roundId,
                FromNodeId: NodeIdFor(workflow, "reviewer"),
                AgentKey: "reviewer",
                AgentVersion: 1,
                OutputPortName: "Rejected",
                OutputRef: new Uri("file:///tmp/reviewer-out.bin"),
                Decision: AgentDecisionKind.Rejected,
                DecisionPayload: JsonDocument.Parse("""{"reasons":["needs work"]}""").RootElement,
                Duration: TimeSpan.FromMilliseconds(1),
                TokenUsage: new Contracts.TokenUsage(0, 0, 0)));

            SpinWaitUntil(() => harness.Published.Select<AgentInvokeRequested>()
                .Any(x => x.Context.Message.AgentKey == "editor"));

            var saga = sagaHarness.Sagas.Contains(traceId)!;
            saga.CurrentAgentKey.Should().Be("editor");

            var logicHistory = saga.GetLogicEvaluationHistory();
            logicHistory[0].OutputPortName.Should().Be("Revise");
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task ScriptedAgent_SetContext_FlowsToDownstreamDispatch()
    {
        var traceId = Guid.NewGuid();
        var roundId = Guid.NewGuid();
        // Simulates the interviewer loop: the scripted agent appends to a transcript
        // and the downstream agent receives the update via context.
        const string script = """
            var prior = (context.transcript || []).slice();
            prior.push({ q: input.question, a: input.answer });
            setContext('transcript', prior);
            setNodePath('NextTurn');
            """;
        var workflow = BuildWorkflowWithScriptedSource(
            key: "scripted-setContext",
            sourceAgentKey: "interviewer",
            sourceScript: script,
            sourceOutputPorts: new[] { "NextTurn" },
            downstream: new Dictionary<string, string>
            {
                ["NextTurn"] = "interviewerRoundTwo"
            });

        var artifactStore = new StubArtifactStore(
            defaultJson: """{"question":"favorite lang?","answer":"Go"}""");
        var harness = BuildHarness(workflow, new Dictionary<string, int>
        {
            ["interviewerRoundTwo"] = 1
        }, artifactStore);
        await harness.Start();
        try
        {
            // Seed workflow with a prior transcript entry to exercise read-modify-write.
            await harness.Bus.Publish(new AgentInvokeRequested(
                TraceId: traceId,
                RoundId: roundId,
                WorkflowKey: workflow.Key,
                WorkflowVersion: workflow.Version,
                NodeId: workflow.StartNode.Id,
                AgentKey: workflow.StartNode.AgentKey!,
                AgentVersion: 1,
                InputRef: new Uri("file:///tmp/in.bin"),
                ContextInputs: new Dictionary<string, JsonElement>
                {
                    ["transcript"] = JsonDocument.Parse("""[{"q":"intro?","a":"hi"}]""").RootElement.Clone()
                }));

            var sagaHarness = harness.GetSagaStateMachineHarness<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
            await sagaHarness.Exists(traceId, s => s.Running);

            await harness.Bus.Publish(new AgentInvocationCompleted(
                TraceId: traceId,
                RoundId: roundId,
                FromNodeId: NodeIdFor(workflow, "interviewer"),
                AgentKey: "interviewer",
                AgentVersion: 1,
                OutputPortName: "Completed",
                OutputRef: new Uri("file:///tmp/interviewer-out.bin"),
                Decision: AgentDecisionKind.Completed,
                DecisionPayload: null,
                Duration: TimeSpan.FromMilliseconds(1),
                TokenUsage: new Contracts.TokenUsage(0, 0, 0)));

            SpinWaitUntil(() => harness.Published.Select<AgentInvokeRequested>()
                .Any(x => x.Context.Message.AgentKey == "interviewerRoundTwo"));

            var downstream = harness.Published.Select<AgentInvokeRequested>()
                .Single(x => x.Context.Message.AgentKey == "interviewerRoundTwo");

            var contextInputs = downstream.Context.Message.ContextInputs;
            contextInputs.Should().ContainKey("transcript");
            var transcript = contextInputs["transcript"];
            transcript.ValueKind.Should().Be(JsonValueKind.Array);
            transcript.GetArrayLength().Should().Be(2);
            transcript[1].GetProperty("q").GetString().Should().Be("favorite lang?");
            transcript[1].GetProperty("a").GetString().Should().Be("Go");
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task ScriptedHitl_RoutesOnInputDecision()
    {
        // HITL node uses the same completion contract; a script attached to a HITL node
        // routes on input.decision (Answer-vs-Exit style fan-out).
        var traceId = Guid.NewGuid();
        var roundId = Guid.NewGuid();
        const string script = """
            if (input.decision === 'Approved') { setNodePath('Answer'); }
            else { setNodePath('Exit'); }
            """;
        var workflow = BuildHitlScriptedWorkflow(
            key: "scripted-hitl",
            startAgentKey: "interviewer",
            hitlAgentKey: "interviewee",
            hitlScript: script,
            hitlOutputPorts: new[] { "Answer", "Exit" },
            downstream: new Dictionary<string, string>
            {
                ["Answer"] = "continueFlow",
                ["Exit"] = "exitFlow"
            });

        var artifactStore = new StubArtifactStore(defaultJson: "{}");
        var harness = BuildHarness(workflow, new Dictionary<string, int>
        {
            ["continueFlow"] = 1,
            ["exitFlow"] = 1
        }, artifactStore);
        await harness.Start();
        try
        {
            await PublishStart(harness, workflow, traceId, roundId);
            var sagaHarness = harness.GetSagaStateMachineHarness<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
            await sagaHarness.Exists(traceId, s => s.Running);

            // Interviewer completes — edge routes to the HITL node.
            await harness.Bus.Publish(new AgentInvocationCompleted(
                TraceId: traceId,
                RoundId: roundId,
                FromNodeId: NodeIdFor(workflow, "interviewer"),
                AgentKey: "interviewer",
                AgentVersion: 1,
                OutputPortName: "Completed",
                OutputRef: new Uri("file:///tmp/interviewer-out.bin"),
                Decision: AgentDecisionKind.Completed,
                DecisionPayload: null,
                Duration: TimeSpan.FromMilliseconds(1),
                TokenUsage: new Contracts.TokenUsage(0, 0, 0)));

            SpinWaitUntil(() => harness.Published.Select<AgentInvokeRequested>()
                .Any(x => x.Context.Message.AgentKey == "interviewee"));

            // HITL completes with Approved — script should route to "Answer".
            await harness.Bus.Publish(new AgentInvocationCompleted(
                TraceId: traceId,
                RoundId: roundId,
                FromNodeId: NodeIdFor(workflow, "interviewee"),
                AgentKey: "interviewee",
                AgentVersion: 1,
                OutputPortName: "Approved",
                OutputRef: new Uri("file:///tmp/hitl-out.bin"),
                Decision: AgentDecisionKind.Approved,
                DecisionPayload: JsonDocument.Parse("""{"answer":"yes"}""").RootElement,
                Duration: TimeSpan.FromMilliseconds(1),
                TokenUsage: new Contracts.TokenUsage(0, 0, 0)));

            SpinWaitUntil(() => harness.Published.Select<AgentInvokeRequested>()
                .Any(x => x.Context.Message.AgentKey == "continueFlow"));

            var saga = sagaHarness.Sagas.Contains(traceId)!;
            saga.CurrentAgentKey.Should().Be("continueFlow");

            var logicHistory = saga.GetLogicEvaluationHistory();
            logicHistory.Should().ContainSingle()
                .Which.OutputPortName.Should().Be("Answer");
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task ScriptedHitl_RoutesOnCustomOutputPortDecision()
    {
        var traceId = Guid.NewGuid();
        var roundId = Guid.NewGuid();
        const string script = """
            if (input.decision === 'Answered') { setNodePath('Answer'); }
            else { setNodePath('Exit'); }
            """;
        var workflow = BuildHitlScriptedWorkflow(
            key: "scripted-hitl-custom-port",
            startAgentKey: "interviewer",
            hitlAgentKey: "interviewee",
            hitlScript: script,
            hitlOutputPorts: new[] { "Answer", "Exit" },
            downstream: new Dictionary<string, string>
            {
                ["Answer"] = "continueFlow",
                ["Exit"] = "exitFlow"
            });

        var artifactStore = new StubArtifactStore(defaultJson: "{}");
        var harness = BuildHarness(workflow, new Dictionary<string, int>
        {
            ["continueFlow"] = 1,
            ["exitFlow"] = 1
        }, artifactStore);
        await harness.Start();
        try
        {
            await PublishStart(harness, workflow, traceId, roundId);
            var sagaHarness = harness.GetSagaStateMachineHarness<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
            await sagaHarness.Exists(traceId, s => s.Running);

            await harness.Bus.Publish(new AgentInvocationCompleted(
                TraceId: traceId,
                RoundId: roundId,
                FromNodeId: NodeIdFor(workflow, "interviewer"),
                AgentKey: "interviewer",
                AgentVersion: 1,
                OutputPortName: "Completed",
                OutputRef: new Uri("file:///tmp/interviewer-out.bin"),
                Decision: AgentDecisionKind.Completed,
                DecisionPayload: null,
                Duration: TimeSpan.FromMilliseconds(1),
                TokenUsage: new Contracts.TokenUsage(0, 0, 0)));

            SpinWaitUntil(() => harness.Published.Select<AgentInvokeRequested>()
                .Any(x => x.Context.Message.AgentKey == "interviewee"));

            await harness.Bus.Publish(new AgentInvocationCompleted(
                TraceId: traceId,
                RoundId: roundId,
                FromNodeId: NodeIdFor(workflow, "interviewee"),
                AgentKey: "interviewee",
                AgentVersion: 1,
                OutputPortName: "Answered",
                OutputRef: new Uri("file:///tmp/hitl-out.bin"),
                Decision: AgentDecisionKind.Completed,
                DecisionPayload: JsonDocument.Parse("""{"answer":"yes","outputPortName":"Answered"}""").RootElement,
                Duration: TimeSpan.FromMilliseconds(1),
                TokenUsage: new Contracts.TokenUsage(0, 0, 0)));

            SpinWaitUntil(() => harness.Published.Select<AgentInvokeRequested>()
                .Any(x => x.Context.Message.AgentKey == "continueFlow"));

            var saga = sagaHarness.Sagas.Contains(traceId)!;
            saga.CurrentAgentKey.Should().Be("continueFlow");

            var logicHistory = saga.GetLogicEvaluationHistory();
            logicHistory.Should().ContainSingle()
                .Which.OutputPortName.Should().Be("Answer");
        }
        finally
        {
            await harness.Stop();
        }
    }

    private static Workflow BuildWorkflowWithScriptedSource(
        string key,
        string sourceAgentKey,
        string sourceScript,
        IReadOnlyList<string> sourceOutputPorts,
        IReadOnlyDictionary<string, string> downstream)
    {
        var nodes = new List<WorkflowNode>();
        var nodeIds = new Dictionary<string, Guid>(StringComparer.Ordinal);

        var sourceId = Guid.NewGuid();
        nodeIds[sourceAgentKey] = sourceId;
        nodes.Add(new WorkflowNode(sourceId, WorkflowNodeKind.Start, sourceAgentKey, 1,
            sourceScript, sourceOutputPorts, 0, 0));

        foreach (var downstreamAgent in downstream.Values.Distinct(StringComparer.Ordinal))
        {
            var id = Guid.NewGuid();
            nodeIds[downstreamAgent] = id;
            nodes.Add(new WorkflowNode(id, WorkflowNodeKind.Agent, downstreamAgent, 1,
                null, AllDecisionPorts, 500, 0));
        }

        var edges = new List<WorkflowEdge>();
        var sortOrder = 0;
        foreach (var (port, agent) in downstream)
        {
            edges.Add(new WorkflowEdge(sourceId, port, nodeIds[agent],
                WorkflowEdge.DefaultInputPort, false, sortOrder++));
        }

        return new Workflow(
            Key: key,
            Version: 1,
            Name: key,
            MaxRoundsPerRound: 5,
            CreatedAtUtc: DateTime.UtcNow,
            Nodes: nodes,
            Edges: edges,
            Inputs: Array.Empty<WorkflowInput>());
    }

    private static Workflow BuildHitlScriptedWorkflow(
        string key,
        string startAgentKey,
        string hitlAgentKey,
        string hitlScript,
        IReadOnlyList<string> hitlOutputPorts,
        IReadOnlyDictionary<string, string> downstream)
    {
        var nodes = new List<WorkflowNode>();
        var nodeIds = new Dictionary<string, Guid>(StringComparer.Ordinal);

        var startId = Guid.NewGuid();
        nodeIds[startAgentKey] = startId;
        nodes.Add(new WorkflowNode(startId, WorkflowNodeKind.Start, startAgentKey, 1,
            null, AllDecisionPorts, 0, 0));

        var hitlId = Guid.NewGuid();
        nodeIds[hitlAgentKey] = hitlId;
        nodes.Add(new WorkflowNode(hitlId, WorkflowNodeKind.Hitl, hitlAgentKey, 1,
            hitlScript, hitlOutputPorts, 250, 0));

        foreach (var downstreamAgent in downstream.Values.Distinct(StringComparer.Ordinal))
        {
            var id = Guid.NewGuid();
            nodeIds[downstreamAgent] = id;
            nodes.Add(new WorkflowNode(id, WorkflowNodeKind.Agent, downstreamAgent, 1,
                null, AllDecisionPorts, 500, 0));
        }

        var edges = new List<WorkflowEdge>
        {
            new(startId, "Completed", hitlId, WorkflowEdge.DefaultInputPort, false, 0)
        };

        var sortOrder = 1;
        foreach (var (port, agent) in downstream)
        {
            edges.Add(new WorkflowEdge(hitlId, port, nodeIds[agent],
                WorkflowEdge.DefaultInputPort, false, sortOrder++));
        }

        return new Workflow(
            Key: key,
            Version: 1,
            Name: key,
            MaxRoundsPerRound: 5,
            CreatedAtUtc: DateTime.UtcNow,
            Nodes: nodes,
            Edges: edges,
            Inputs: Array.Empty<WorkflowInput>());
    }

    [Fact]
    public async Task FailedDecisionWithRetryEdge_ShouldIncludeRetryContextOnHandoff()
    {
        var traceId = Guid.NewGuid();
        var roundId = Guid.NewGuid();
        var workflow = BuildWorkflow(
            key: "retry",
            maxRounds: 5,
            startAgentKey: "reviewer",
            edges:
            [
                Edge("reviewer", RuntimeDecisionKind.Failed, "reviewer", rotatesRound: false)
            ]);

        var harness = BuildHarness(workflow, new Dictionary<string, int> { ["reviewer"] = 2 });
        await harness.Start();
        try
        {
            await PublishStart(harness, workflow, traceId, roundId, agentVersion: 2);

            var sagaHarness = harness.GetSagaStateMachineHarness<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
            await sagaHarness.Exists(traceId, s => s.Running);

            var failurePayload = JsonDocument.Parse("""
            {
              "kind": "Failed",
              "reason": "tool_call_budget_exceeded",
              "failure_context": {
                "reason": "tool_call_budget_exceeded",
                "last_output": "Attempted 10 refactors without success.",
                "tool_calls_executed": 10
              }
            }
            """).RootElement;

            await harness.Bus.Publish(new AgentInvocationCompleted(
                TraceId: traceId,
                RoundId: roundId,
                FromNodeId: NodeIdFor(workflow, "reviewer"),
                AgentKey: "reviewer",
                AgentVersion: 2,
                OutputPortName: AgentDecisionPorts.ToPortName(AgentDecisionKind.Failed),
                OutputRef: new Uri("file:///tmp/reviewer-out.bin"),
                Decision: AgentDecisionKind.Failed,
                DecisionPayload: failurePayload,
                Duration: TimeSpan.FromMilliseconds(1),
                TokenUsage: new Contracts.TokenUsage(0, 0, 0)));

            SpinWaitUntil(() => harness.Published.Select<AgentInvokeRequested>()
                .Count(x => x.Context.Message.AgentKey == "reviewer") >= 2);

            var retryPublish = harness.Published.Select<AgentInvokeRequested>()
                .Where(x => x.Context.Message.AgentKey == "reviewer")
                .Skip(1)
                .Single();

            retryPublish.Context.Message.RetryContext.Should().NotBeNull();
            retryPublish.Context.Message.RetryContext!.AttemptNumber.Should().Be(2, "first failure + 1 = 2nd attempt");
            retryPublish.Context.Message.RetryContext.PriorFailureReason.Should().Be("tool_call_budget_exceeded");
            retryPublish.Context.Message.RetryContext.PriorAttemptSummary.Should().Contain("Attempted 10 refactors without success.");
            retryPublish.Context.Message.RetryContext.PriorAttemptSummary.Should().Contain("Tool calls executed: 10");
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task SubflowNode_ShouldPublishSubflowInvokeRequestedWithParentLinkageAndDepth()
    {
        // S3: parent saga reaches a Subflow node and emits SubflowInvokeRequested with the
        // parent linkage populated. Depth=1 because the parent saga is at the top level.
        var traceId = Guid.NewGuid();
        var roundId = Guid.NewGuid();
        var startNodeId = Guid.NewGuid();
        var subflowNodeId = Guid.NewGuid();

        var workflow = BuildWorkflowWithSubflow(
            workflowKey: "parent-with-subflow",
            startNodeId: startNodeId,
            startAgentKey: "kickoff",
            subflowNodeId: subflowNodeId,
            subflowKey: "shared-utility",
            subflowVersion: 7);

        var harness = BuildHarness(workflow, new Dictionary<string, int>());
        await harness.Start();
        try
        {
            await PublishStart(harness, workflow, traceId, roundId);
            var sagaHarness = harness.GetSagaStateMachineHarness<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
            await sagaHarness.Exists(traceId, x => x.Running);

            await harness.Bus.Publish(BuildCompletion(workflow, traceId, roundId, "kickoff", 1, AgentDecisionKind.Completed));

            var dispatches = await WaitForPublishedAsync<SubflowInvokeRequested>(harness, expectedCount: 1);
            dispatches.Should().HaveCount(1);

            var dispatched = dispatches[0].Context.Message;
            dispatched.ParentTraceId.Should().Be(traceId);
            dispatched.ParentNodeId.Should().Be(subflowNodeId);
            dispatched.ParentRoundId.Should().Be(roundId, "non-rotating edge keeps the parent's round id");
            dispatched.ChildTraceId.Should().NotBeEmpty();
            dispatched.ChildTraceId.Should().NotBe(traceId, "child saga gets a fresh trace id");
            dispatched.SubflowKey.Should().Be("shared-utility");
            dispatched.SubflowVersion.Should().Be(7);
            dispatched.Depth.Should().Be(1, "top-level saga has SubflowDepth=0, child = 0 + 1");
            dispatched.SharedContext.Should().BeEmpty(
                "no setGlobal writes have occurred and no API caller seeded global at start");

            var saga = sagaHarness.Sagas.Contains(traceId);
            saga.Should().NotBeNull();
            saga!.CurrentState.Should().Be(nameof(WorkflowSagaStateMachine.Running),
                "parent saga stays Running while child executes");
            saga.CurrentNodeId.Should().Be(subflowNodeId);
            saga.SubflowDepth.Should().Be(0, "parent's own depth is unchanged by spawning a child");
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task SubflowNode_AtMaxDepthShouldFailParentSagaWithoutPublishing()
    {
        // S3 depth-cap: when spawning a child would exceed MaxSubflowDepth (3), the parent saga
        // transitions to Failed with reason "SubflowDepthExceeded" and emits no
        // SubflowInvokeRequested. Seeded by mutating the parent's SubflowDepth to 3 after the
        // saga is created (S4 will provide the natural mechanism to seed via SubflowInvokeRequested).
        var traceId = Guid.NewGuid();
        var roundId = Guid.NewGuid();
        var startNodeId = Guid.NewGuid();
        var subflowNodeId = Guid.NewGuid();

        var workflow = BuildWorkflowWithSubflow(
            workflowKey: "depth-cap",
            startNodeId: startNodeId,
            startAgentKey: "kickoff",
            subflowNodeId: subflowNodeId,
            subflowKey: "shared-utility",
            subflowVersion: 7);

        var harness = BuildHarness(workflow, new Dictionary<string, int>());
        await harness.Start();
        try
        {
            await PublishStart(harness, workflow, traceId, roundId);
            var sagaHarness = harness.GetSagaStateMachineHarness<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
            await sagaHarness.Exists(traceId, x => x.Running);

            // Seed SubflowDepth = MaxSubflowDepth so the next spawn would be at depth 4.
            var saga = sagaHarness.Sagas.Contains(traceId)!;
            saga.SubflowDepth = WorkflowSagaStateMachine.MaxSubflowDepth;

            await harness.Bus.Publish(BuildCompletion(workflow, traceId, roundId, "kickoff", 1, AgentDecisionKind.Completed));

            await sagaHarness.Exists(traceId, x => x.Failed);

            var failed = sagaHarness.Sagas.Contains(traceId)!;
            failed.CurrentState.Should().Be(nameof(WorkflowSagaStateMachine.Failed));
            failed.FailureReason.Should().NotBeNullOrWhiteSpace();
            failed.FailureReason!.Should().Contain("SubflowDepthExceeded");
            failed.FailureReason.Should().Contain(WorkflowSagaStateMachine.MaxSubflowDepth.ToString());

            (await harness.Published.Any<SubflowInvokeRequested>()).Should().BeFalse(
                "depth-cap rejection must not publish a child dispatch");
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task SubflowCompleted_ShouldMergeGlobalAndTerminateParentWhenNoDownstreamEdge()
    {
        // S5: a SubflowCompleted with port "Completed" and no downstream edge from the Subflow
        // node terminates the parent in the Completed state. The child's final SharedContext is
        // shallow-merged into the parent's global before routing, and a synthetic decision is
        // appended to the parent's history.
        var traceId = Guid.NewGuid();
        var parentRoundId = Guid.NewGuid();
        var startNodeId = Guid.NewGuid();
        var subflowNodeId = Guid.NewGuid();

        var workflow = BuildWorkflowWithSubflow(
            workflowKey: "parent-resume",
            startNodeId: startNodeId,
            startAgentKey: "kickoff",
            subflowNodeId: subflowNodeId,
            subflowKey: "shared-utility",
            subflowVersion: 7);

        var harness = BuildHarness(workflow, new Dictionary<string, int>());
        await harness.Start();
        try
        {
            await PublishStart(harness, workflow, traceId, parentRoundId);
            var sagaHarness = harness.GetSagaStateMachineHarness<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
            await sagaHarness.Exists(traceId, x => x.Running);

            // Drive the parent to dispatch the Subflow (round id stays = parentRoundId because
            // the edge does not rotate).
            await harness.Bus.Publish(BuildCompletion(workflow, traceId, parentRoundId, "kickoff", 1, AgentDecisionKind.Completed));
            await WaitForPublishedAsync<SubflowInvokeRequested>(harness, expectedCount: 1);

            // Sanity: parent is now sitting on the Subflow node.
            var parent = sagaHarness.Sagas.Contains(traceId)!;
            parent.CurrentNodeId.Should().Be(subflowNodeId);
            parent.CurrentRoundId.Should().Be(parentRoundId);

            // Synthesize the child's completion with a SharedContext that should propagate.
            var childGlobal = new Dictionary<string, JsonElement>
            {
                ["resolvedSpec"] = JsonDocument.Parse("""{"engine":"markdown"}""").RootElement.Clone(),
                ["fromChild"] = JsonDocument.Parse("\"yes\"").RootElement.Clone(),
            };

            await harness.Bus.Publish(new SubflowCompleted(
                ParentTraceId: traceId,
                ParentNodeId: subflowNodeId,
                ParentRoundId: parentRoundId,
                ChildTraceId: Guid.NewGuid(),
                OutputPortName: "Completed",
                OutputRef: new Uri("file:///tmp/child-final.bin"),
                SharedContext: childGlobal));

            await sagaHarness.Exists(traceId, x => x.Completed);

            var resumed = sagaHarness.Sagas.Contains(traceId)!;
            resumed.CurrentState.Should().Be(nameof(WorkflowSagaStateMachine.Completed));
            resumed.GlobalInputsJson.Should().NotBeNullOrWhiteSpace();
            resumed.GlobalInputsJson!.Should().Contain("resolvedSpec");
            resumed.GlobalInputsJson.Should().Contain("fromChild");

            // Decision history should include the synthetic Subflow completion record.
            var decisions = resumed.GetDecisionHistory();
            decisions.Should().Contain(d =>
                d.NodeId == subflowNodeId
                && d.OutputPortName == "Completed"
                && d.OutputRef == "file:///tmp/child-final.bin");
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task SubflowCompleted_FailedPortShouldTerminateParentWithFailureReason()
    {
        // S5: a SubflowCompleted with port "Failed" and no Failed-edge from the Subflow node
        // terminates the parent saga in Failed with a clear FailureReason.
        var traceId = Guid.NewGuid();
        var parentRoundId = Guid.NewGuid();
        var startNodeId = Guid.NewGuid();
        var subflowNodeId = Guid.NewGuid();

        var workflow = BuildWorkflowWithSubflow(
            workflowKey: "parent-failed-resume",
            startNodeId: startNodeId,
            startAgentKey: "kickoff",
            subflowNodeId: subflowNodeId,
            subflowKey: "shared-utility",
            subflowVersion: 7);

        var harness = BuildHarness(workflow, new Dictionary<string, int>());
        await harness.Start();
        try
        {
            await PublishStart(harness, workflow, traceId, parentRoundId);
            var sagaHarness = harness.GetSagaStateMachineHarness<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
            await sagaHarness.Exists(traceId, x => x.Running);
            await harness.Bus.Publish(BuildCompletion(workflow, traceId, parentRoundId, "kickoff", 1, AgentDecisionKind.Completed));
            await WaitForPublishedAsync<SubflowInvokeRequested>(harness, expectedCount: 1);

            await harness.Bus.Publish(new SubflowCompleted(
                ParentTraceId: traceId,
                ParentNodeId: subflowNodeId,
                ParentRoundId: parentRoundId,
                ChildTraceId: Guid.NewGuid(),
                OutputPortName: "Failed",
                OutputRef: new Uri("file:///tmp/child-failed.bin"),
                SharedContext: new Dictionary<string, JsonElement>()));

            await sagaHarness.Exists(traceId, x => x.Failed);

            var resumed = sagaHarness.Sagas.Contains(traceId)!;
            resumed.CurrentState.Should().Be(nameof(WorkflowSagaStateMachine.Failed));
            resumed.FailureReason.Should().NotBeNullOrWhiteSpace();
            resumed.FailureReason!.Should().Contain("Failed");
            resumed.FailureReason.Should().Contain(subflowNodeId.ToString());
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task SubflowCompleted_StaleParentRoundShouldBeIgnored()
    {
        // S5 stale-round defense: a SubflowCompleted whose ParentRoundId no longer matches the
        // parent saga's CurrentRoundId is ignored — no state change, no transition, no merge.
        var traceId = Guid.NewGuid();
        var parentRoundId = Guid.NewGuid();
        var startNodeId = Guid.NewGuid();
        var subflowNodeId = Guid.NewGuid();

        var workflow = BuildWorkflowWithSubflow(
            workflowKey: "parent-stale-round",
            startNodeId: startNodeId,
            startAgentKey: "kickoff",
            subflowNodeId: subflowNodeId,
            subflowKey: "shared-utility",
            subflowVersion: 7);

        var harness = BuildHarness(workflow, new Dictionary<string, int>());
        await harness.Start();
        try
        {
            await PublishStart(harness, workflow, traceId, parentRoundId);
            var sagaHarness = harness.GetSagaStateMachineHarness<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
            await sagaHarness.Exists(traceId, x => x.Running);
            await harness.Bus.Publish(BuildCompletion(workflow, traceId, parentRoundId, "kickoff", 1, AgentDecisionKind.Completed));
            await WaitForPublishedAsync<SubflowInvokeRequested>(harness, expectedCount: 1);

            await harness.Bus.Publish(new SubflowCompleted(
                ParentTraceId: traceId,
                ParentNodeId: subflowNodeId,
                ParentRoundId: Guid.NewGuid(), // intentionally wrong round
                ChildTraceId: Guid.NewGuid(),
                OutputPortName: "Completed",
                OutputRef: new Uri("file:///tmp/stale.bin"),
                SharedContext: new Dictionary<string, JsonElement>
                {
                    ["shouldNotMerge"] = JsonDocument.Parse("\"true\"").RootElement.Clone(),
                }));

            // Give the bus a moment to process; saga must remain in Running.
            await Task.Delay(500);
            var saga = sagaHarness.Sagas.Contains(traceId)!;
            saga.CurrentState.Should().Be(nameof(WorkflowSagaStateMachine.Running));
            (saga.GlobalInputsJson ?? string.Empty).Should().NotContain("shouldNotMerge",
                "stale-round messages must not mutate the parent's global");
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task ReviewLoopCompleted_ShouldCarryGlobalAcrossRoundsAndMergeIntoParentOnExit()
    {
        // Slice 4: a child's setGlobal writes during round N must be visible to round N+1 (via
        // SharedContext on the next SubflowInvokeRequested) and must survive into the parent's
        // global bag after the loop exits.
        var traceId = Guid.NewGuid();
        var parentRoundId = Guid.NewGuid();
        var startNodeId = Guid.NewGuid();
        var reviewLoopNodeId = Guid.NewGuid();

        var workflow = BuildWorkflowWithReviewLoop(
            workflowKey: "review-loop-merge",
            startNodeId: startNodeId,
            startAgentKey: "kickoff",
            reviewLoopNodeId: reviewLoopNodeId,
            subflowKey: "critique-revise",
            subflowVersion: 1,
            maxRounds: 2);

        var harness = BuildHarness(workflow, new Dictionary<string, int>());
        await harness.Start();
        try
        {
            await PublishStart(harness, workflow, traceId, parentRoundId);
            var sagaHarness = harness.GetSagaStateMachineHarness<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
            await sagaHarness.Exists(traceId, x => x.Running);

            // Drive Start → ReviewLoop; round 1 SubflowInvokeRequested fires.
            await harness.Bus.Publish(BuildCompletion(workflow, traceId, parentRoundId, "kickoff", 1, AgentDecisionKind.Completed));

            var round1Requests = await WaitForPublishedAsync<SubflowInvokeRequested>(harness, expectedCount: 1);
            round1Requests.Should().HaveCount(1);
            var round1 = round1Requests[0].Context.Message;
            round1.ReviewRound.Should().Be(1);
            round1.ReviewMaxRounds.Should().Be(2);

            // Child round 1 finishes Rejected with setGlobal('counter', 1).
            var round1Global = new Dictionary<string, JsonElement>
            {
                ["counter"] = JsonDocument.Parse("1").RootElement.Clone(),
            };
            await harness.Bus.Publish(new SubflowCompleted(
                ParentTraceId: traceId,
                ParentNodeId: reviewLoopNodeId,
                ParentRoundId: parentRoundId,
                ChildTraceId: round1.ChildTraceId,
                OutputPortName: "Completed",
                OutputRef: new Uri("file:///tmp/round1-out.bin"),
                SharedContext: round1Global,
                Decision: AgentDecisionKind.Rejected,
                ReviewRound: 1));

            // Parent must not terminate — it should spawn round 2 instead.
            var round2Requests = await WaitForPublishedAsync<SubflowInvokeRequested>(harness, expectedCount: 2);
            round2Requests.Should().HaveCount(2);
            var round2 = round2Requests[1].Context.Message;
            round2.ReviewRound.Should().Be(2, "Rejected with rounds remaining advances the round counter");
            round2.ReviewMaxRounds.Should().Be(2);
            round2.InputRef.Should().Be(new Uri("file:///tmp/round1-out.bin"),
                "round N+1 input = round N's output artifact");
            round2.SharedContext.Should().ContainKey("counter");
            round2.SharedContext["counter"].GetInt32().Should().Be(1,
                "round 2 must see round 1's setGlobal writes through its SharedContext snapshot");

            // Child round 2 approves with an additional global write.
            var round2Global = new Dictionary<string, JsonElement>
            {
                ["counter"] = JsonDocument.Parse("2").RootElement.Clone(),
                ["done"] = JsonDocument.Parse("true").RootElement.Clone(),
            };
            await harness.Bus.Publish(new SubflowCompleted(
                ParentTraceId: traceId,
                ParentNodeId: reviewLoopNodeId,
                ParentRoundId: parentRoundId,
                ChildTraceId: round2.ChildTraceId,
                OutputPortName: "Completed",
                OutputRef: new Uri("file:///tmp/round2-out.bin"),
                SharedContext: round2Global,
                Decision: AgentDecisionKind.Approved,
                ReviewRound: 2));

            // Approved port has no outgoing edge in this workflow, so the parent falls through
            // to the Completed terminal state.
            await sagaHarness.Exists(traceId, x => x.Completed);

            var resumed = sagaHarness.Sagas.Contains(traceId)!;
            resumed.CurrentState.Should().Be(nameof(WorkflowSagaStateMachine.Completed));
            resumed.GlobalInputsJson.Should().NotBeNullOrWhiteSpace();
            resumed.GlobalInputsJson!.Should().Contain("\"counter\":2",
                "the parent's final global must reflect the last round's write");
            resumed.GlobalInputsJson.Should().Contain("done",
                "keys added only in the final round must also be merged up");
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Theory]
    [InlineData(AgentDecisionKind.Approved)]
    [InlineData(AgentDecisionKind.Completed)]
    public async Task ReviewLoopCompleted_ApprovedOrCompletedOnRound1_ShouldExitApprovedPort(
        AgentDecisionKind childDecision)
    {
        // Slice 10 scenarios 1 + 2: the permissive-mapping leg of ResolveReviewLoopOutcome.
        // Both Approved and Completed at any round exit the parent via the Approved port; with
        // no downstream edge from Approved, the parent falls through to Completed.
        var traceId = Guid.NewGuid();
        var parentRoundId = Guid.NewGuid();
        var startNodeId = Guid.NewGuid();
        var reviewLoopNodeId = Guid.NewGuid();

        var workflow = BuildWorkflowWithReviewLoop(
            "rl-approved-round1", startNodeId, "kickoff", reviewLoopNodeId,
            subflowKey: "critique-revise", subflowVersion: 1, maxRounds: 3);

        var harness = BuildHarness(workflow, new Dictionary<string, int>());
        await harness.Start();
        try
        {
            await PublishStart(harness, workflow, traceId, parentRoundId);
            var sagaHarness = harness.GetSagaStateMachineHarness<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
            await sagaHarness.Exists(traceId, x => x.Running);

            await harness.Bus.Publish(BuildCompletion(workflow, traceId, parentRoundId, "kickoff", 1, AgentDecisionKind.Completed));
            var round1 = (await WaitForPublishedAsync<SubflowInvokeRequested>(harness, expectedCount: 1))[0].Context.Message;

            await harness.Bus.Publish(new SubflowCompleted(
                ParentTraceId: traceId,
                ParentNodeId: reviewLoopNodeId,
                ParentRoundId: parentRoundId,
                ChildTraceId: round1.ChildTraceId,
                OutputPortName: "Completed",
                OutputRef: new Uri("file:///tmp/round1-out.bin"),
                SharedContext: new Dictionary<string, JsonElement>(),
                Decision: childDecision,
                ReviewRound: 1));

            await sagaHarness.Exists(traceId, x => x.Completed);

            // Exactly one round spawned — no second round for an approved/completed outcome.
            var allRequests = await WaitForPublishedAsync<SubflowInvokeRequested>(harness, expectedCount: 1);
            allRequests.Should().HaveCount(1);

            var resumed = sagaHarness.Sagas.Contains(traceId)!;
            resumed.GetDecisionHistory().Should()
                .Contain(d => d.NodeId == reviewLoopNodeId && d.OutputPortName == "Approved",
                    "synthetic parent decision for the ReviewLoop must record the mapped port");
        }
        finally { await harness.Stop(); }
    }

    [Fact]
    public async Task ReviewLoopCompleted_RejectOnEveryRound_ShouldExitExhaustedPort()
    {
        // Slice 10 scenario 4: with MaxRounds=2, a Rejected on round 1 spawns round 2; a
        // Rejected on round 2 has no rounds left and exits via the Exhausted port (which has
        // no downstream edge here, so the parent transitions to Failed with a clear reason).
        var traceId = Guid.NewGuid();
        var parentRoundId = Guid.NewGuid();
        var startNodeId = Guid.NewGuid();
        var reviewLoopNodeId = Guid.NewGuid();

        var workflow = BuildWorkflowWithReviewLoop(
            "rl-exhausted", startNodeId, "kickoff", reviewLoopNodeId,
            subflowKey: "critique-revise", subflowVersion: 1, maxRounds: 2);

        var harness = BuildHarness(workflow, new Dictionary<string, int>());
        await harness.Start();
        try
        {
            await PublishStart(harness, workflow, traceId, parentRoundId);
            var sagaHarness = harness.GetSagaStateMachineHarness<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
            await sagaHarness.Exists(traceId, x => x.Running);

            await harness.Bus.Publish(BuildCompletion(workflow, traceId, parentRoundId, "kickoff", 1, AgentDecisionKind.Completed));
            var round1 = (await WaitForPublishedAsync<SubflowInvokeRequested>(harness, expectedCount: 1))[0].Context.Message;

            // Round 1: Rejected → next round.
            await harness.Bus.Publish(new SubflowCompleted(
                ParentTraceId: traceId, ParentNodeId: reviewLoopNodeId, ParentRoundId: parentRoundId,
                ChildTraceId: round1.ChildTraceId, OutputPortName: "Completed",
                OutputRef: new Uri("file:///tmp/r1-out.bin"),
                SharedContext: new Dictionary<string, JsonElement>(),
                Decision: AgentDecisionKind.Rejected, ReviewRound: 1));

            var round2 = (await WaitForPublishedAsync<SubflowInvokeRequested>(harness, expectedCount: 2))[1].Context.Message;
            round2.ReviewRound.Should().Be(2);

            // Round 2 (last): Rejected → Exhausted port (no outgoing edge → Failed terminal).
            await harness.Bus.Publish(new SubflowCompleted(
                ParentTraceId: traceId, ParentNodeId: reviewLoopNodeId, ParentRoundId: parentRoundId,
                ChildTraceId: round2.ChildTraceId, OutputPortName: "Completed",
                OutputRef: new Uri("file:///tmp/r2-out.bin"),
                SharedContext: new Dictionary<string, JsonElement>(),
                Decision: AgentDecisionKind.Rejected, ReviewRound: 2));

            await sagaHarness.Exists(traceId, x => x.Failed);

            var resumed = sagaHarness.Sagas.Contains(traceId)!;
            resumed.FailureReason.Should().Contain("Exhausted",
                "an unwired Exhausted port produces a Failed terminal with the port name surfaced");

            resumed.GetDecisionHistory().Should()
                .Contain(d => d.NodeId == reviewLoopNodeId && d.OutputPortName == "Exhausted");
        }
        finally { await harness.Stop(); }
    }

    [Fact]
    public async Task ReviewLoopCompleted_FailedOnRound2_ShouldExitFailedPort_AndKeepRound1GlobalMerged()
    {
        // Slice 10 scenario 5: a Failed return from round 2 exits the Failed port (no edge →
        // Failed terminal). Round 1's setGlobal writes must still be visible on the parent's
        // global, because the merge happens inline with each SubflowCompleted.
        var traceId = Guid.NewGuid();
        var parentRoundId = Guid.NewGuid();
        var startNodeId = Guid.NewGuid();
        var reviewLoopNodeId = Guid.NewGuid();

        var workflow = BuildWorkflowWithReviewLoop(
            "rl-failed-after-rounds", startNodeId, "kickoff", reviewLoopNodeId,
            subflowKey: "critique-revise", subflowVersion: 1, maxRounds: 3);

        var harness = BuildHarness(workflow, new Dictionary<string, int>());
        await harness.Start();
        try
        {
            await PublishStart(harness, workflow, traceId, parentRoundId);
            var sagaHarness = harness.GetSagaStateMachineHarness<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
            await sagaHarness.Exists(traceId, x => x.Running);

            await harness.Bus.Publish(BuildCompletion(workflow, traceId, parentRoundId, "kickoff", 1, AgentDecisionKind.Completed));
            var round1 = (await WaitForPublishedAsync<SubflowInvokeRequested>(harness, expectedCount: 1))[0].Context.Message;

            // Round 1: Rejected with a setGlobal write; next round spawns.
            await harness.Bus.Publish(new SubflowCompleted(
                ParentTraceId: traceId, ParentNodeId: reviewLoopNodeId, ParentRoundId: parentRoundId,
                ChildTraceId: round1.ChildTraceId, OutputPortName: "Completed",
                OutputRef: new Uri("file:///tmp/r1.bin"),
                SharedContext: new Dictionary<string, JsonElement>
                {
                    ["fromRound1"] = JsonDocument.Parse("\"carried\"").RootElement.Clone()
                },
                Decision: AgentDecisionKind.Rejected, ReviewRound: 1));

            var round2 = (await WaitForPublishedAsync<SubflowInvokeRequested>(harness, expectedCount: 2))[1].Context.Message;

            // Round 2: Failed → Failed port (no outgoing edge → Failed terminal).
            await harness.Bus.Publish(new SubflowCompleted(
                ParentTraceId: traceId, ParentNodeId: reviewLoopNodeId, ParentRoundId: parentRoundId,
                ChildTraceId: round2.ChildTraceId, OutputPortName: "Failed",
                OutputRef: new Uri("file:///tmp/r2-failed.bin"),
                SharedContext: new Dictionary<string, JsonElement>(),
                Decision: AgentDecisionKind.Failed, ReviewRound: 2));

            await sagaHarness.Exists(traceId, x => x.Failed);

            var resumed = sagaHarness.Sagas.Contains(traceId)!;
            resumed.GlobalInputsJson.Should().NotBeNullOrWhiteSpace();
            resumed.GlobalInputsJson!.Should().Contain("fromRound1",
                "round 1's setGlobal write must survive even when a later round fails");
        }
        finally { await harness.Stop(); }
    }

    [Fact]
    public async Task ReviewLoopCompleted_EscalatedFromChild_ShouldExitFailedPort()
    {
        // Slice 10 scenario 6: if the child saga hits its own escalation node, the completion
        // arrives with OutputPortName = "Escalated". ReviewLoop collapses Escalated to the
        // Failed port regardless of Decision metadata — escalation signals "went sideways,"
        // not "please revise."
        var traceId = Guid.NewGuid();
        var parentRoundId = Guid.NewGuid();
        var startNodeId = Guid.NewGuid();
        var reviewLoopNodeId = Guid.NewGuid();

        var workflow = BuildWorkflowWithReviewLoop(
            "rl-escalated", startNodeId, "kickoff", reviewLoopNodeId,
            subflowKey: "critique-revise", subflowVersion: 1, maxRounds: 3);

        var harness = BuildHarness(workflow, new Dictionary<string, int>());
        await harness.Start();
        try
        {
            await PublishStart(harness, workflow, traceId, parentRoundId);
            var sagaHarness = harness.GetSagaStateMachineHarness<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
            await sagaHarness.Exists(traceId, x => x.Running);

            await harness.Bus.Publish(BuildCompletion(workflow, traceId, parentRoundId, "kickoff", 1, AgentDecisionKind.Completed));
            var round1 = (await WaitForPublishedAsync<SubflowInvokeRequested>(harness, expectedCount: 1))[0].Context.Message;

            await harness.Bus.Publish(new SubflowCompleted(
                ParentTraceId: traceId, ParentNodeId: reviewLoopNodeId, ParentRoundId: parentRoundId,
                ChildTraceId: round1.ChildTraceId, OutputPortName: "Escalated",
                OutputRef: new Uri("file:///tmp/escalated.bin"),
                SharedContext: new Dictionary<string, JsonElement>(),
                Decision: null, ReviewRound: 1));

            await sagaHarness.Exists(traceId, x => x.Failed);

            var resumed = sagaHarness.Sagas.Contains(traceId)!;
            resumed.GetDecisionHistory().Should()
                .Contain(d => d.NodeId == reviewLoopNodeId && d.OutputPortName == "Failed",
                    "Escalated from a ReviewLoop child must surface on the parent as the Failed port");
        }
        finally { await harness.Stop(); }
    }

    [Fact]
    public async Task ReviewLoopNode_ShouldFailFast_WhenSpawningWouldExceedSubflowDepth()
    {
        // Slice 10 scenario 8: ReviewLoop nodes count toward MaxSubflowDepth the same way
        // plain Subflow nodes do. Seed SubflowDepth = MaxSubflowDepth so the next spawn would
        // land at depth 4; the parent must fail with SubflowDepthExceeded and emit no
        // SubflowInvokeRequested.
        var traceId = Guid.NewGuid();
        var roundId = Guid.NewGuid();
        var startNodeId = Guid.NewGuid();
        var reviewLoopNodeId = Guid.NewGuid();

        var workflow = BuildWorkflowWithReviewLoop(
            "rl-depth-cap", startNodeId, "kickoff", reviewLoopNodeId,
            subflowKey: "critique-revise", subflowVersion: 1, maxRounds: 3);

        var harness = BuildHarness(workflow, new Dictionary<string, int>());
        await harness.Start();
        try
        {
            await PublishStart(harness, workflow, traceId, roundId);
            var sagaHarness = harness.GetSagaStateMachineHarness<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
            await sagaHarness.Exists(traceId, x => x.Running);

            var saga = sagaHarness.Sagas.Contains(traceId)!;
            saga.SubflowDepth = WorkflowSagaStateMachine.MaxSubflowDepth;

            await harness.Bus.Publish(BuildCompletion(workflow, traceId, roundId, "kickoff", 1, AgentDecisionKind.Completed));

            await sagaHarness.Exists(traceId, x => x.Failed);

            var failed = sagaHarness.Sagas.Contains(traceId)!;
            failed.FailureReason.Should().NotBeNullOrWhiteSpace();
            failed.FailureReason!.Should().Contain("SubflowDepthExceeded");
            failed.FailureReason.Should().Contain("ReviewLoop",
                "depth-exceeded reason names the target node kind for operator diagnostics");

            (await harness.Published.Any<SubflowInvokeRequested>()).Should().BeFalse(
                "depth-cap rejection must not spawn a child dispatch");
        }
        finally { await harness.Stop(); }
    }

    [Fact]
    public async Task SubflowInvokeRequested_ShouldCreateChildSagaWithLinkageAndDispatchStart()
    {
        // S4: a SubflowInvokeRequested directly creates a child saga in Running state with the
        // parent linkage populated from the message, the global snapshot stored as the child's
        // global_inputs_json (via SerializeContextInputs), and an AgentInvokeRequested published
        // for the child workflow's Start node.
        var parentTraceId = Guid.NewGuid();
        var parentNodeId = Guid.NewGuid();
        var parentRoundId = Guid.NewGuid();
        var childTraceId = Guid.NewGuid();

        // Child workflow: just a Start node so we can observe the dispatch without running real
        // routing logic.
        var childStartNodeId = Guid.NewGuid();
        var childWorkflow = new Workflow(
            Key: "child-flow",
            Version: 3,
            Name: "child",
            MaxRoundsPerRound: 5,
            CreatedAtUtc: DateTime.UtcNow,
            Nodes: new[]
            {
                new WorkflowNode(childStartNodeId, WorkflowNodeKind.Start, "child-start-agent",
                    AgentVersion: null, Script: null, OutputPorts: AllDecisionPorts, LayoutX: 0, LayoutY: 0),
            },
            Edges: Array.Empty<WorkflowEdge>(),
            Inputs: Array.Empty<WorkflowInput>());

        var harness = BuildHarness(childWorkflow, new Dictionary<string, int> { ["child-start-agent"] = 11 });
        await harness.Start();
        try
        {
            var sharedContext = new Dictionary<string, JsonElement>
            {
                ["sharedFlag"] = JsonDocument.Parse("\"on\"").RootElement.Clone(),
            };

            await harness.Bus.Publish(new SubflowInvokeRequested(
                ParentTraceId: parentTraceId,
                ParentNodeId: parentNodeId,
                ParentRoundId: parentRoundId,
                ChildTraceId: childTraceId,
                SubflowKey: "child-flow",
                SubflowVersion: 3,
                InputRef: new Uri("file:///tmp/child-input.bin"),
                SharedContext: sharedContext,
                Depth: 1));

            var sagaHarness = harness.GetSagaStateMachineHarness<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
            var sagaInstance = await sagaHarness.Exists(childTraceId, x => x.Running);
            sagaInstance.Should().NotBeNull();

            var childInvocations = await WaitForPublishedAsync<AgentInvokeRequested>(
                harness,
                expectedCount: 1,
                timeout: TimeSpan.FromSeconds(5));
            childInvocations.Should().HaveCount(1);

            var dispatch = childInvocations[0].Context.Message;
            dispatch.TraceId.Should().Be(childTraceId, "child saga uses ChildTraceId as its TraceId");
            dispatch.NodeId.Should().Be(childStartNodeId);
            dispatch.AgentKey.Should().Be("child-start-agent");
            dispatch.AgentVersion.Should().Be(11, "Start node's AgentVersion was null so latest pinned via repo (=11)");
            dispatch.WorkflowKey.Should().Be("child-flow");
            dispatch.WorkflowVersion.Should().Be(3);
            dispatch.InputRef.Should().Be(new Uri("file:///tmp/child-input.bin"));
            dispatch.ContextInputs.Should().BeEmpty(
                "inherited parent state belongs on GlobalContext, not ContextInputs — the child's local context starts empty");
            dispatch.GlobalContext.Should().NotBeNull();
            dispatch.GlobalContext!.Should().ContainKey("sharedFlag",
                "child Start must see inherited state under {{global.*}} from the first node onward");

            var saga = sagaHarness.Sagas.Contains(childTraceId)!;
            saga.CurrentState.Should().Be(nameof(WorkflowSagaStateMachine.Running));
            saga.TraceId.Should().Be(childTraceId);
            saga.WorkflowKey.Should().Be("child-flow");
            saga.WorkflowVersion.Should().Be(3);
            saga.CurrentNodeId.Should().Be(childStartNodeId);
            saga.CurrentAgentKey.Should().Be("child-start-agent");
            saga.ParentTraceId.Should().Be(parentTraceId);
            saga.ParentNodeId.Should().Be(parentNodeId);
            saga.ParentRoundId.Should().Be(parentRoundId);
            saga.SubflowDepth.Should().Be(1);
            saga.GetPinnedVersion("child-start-agent").Should().Be(11);
            saga.CurrentInputRef.Should().Be("file:///tmp/child-input.bin");
            saga.GlobalInputsJson.Should().NotBeNullOrWhiteSpace();
            saga.GlobalInputsJson!.Should().Contain("sharedFlag");
            saga.InputsJson.Should().Be("{}", "child's local context starts empty");
        }
        finally
        {
            await harness.Stop();
        }
    }

    private static Workflow BuildWorkflowWithSubflow(
        string workflowKey,
        Guid startNodeId,
        string startAgentKey,
        Guid subflowNodeId,
        string subflowKey,
        int? subflowVersion)
    {
        var nodes = new List<WorkflowNode>
        {
            new(startNodeId, WorkflowNodeKind.Start, startAgentKey, AgentVersion: null, Script: null,
                OutputPorts: AllDecisionPorts, LayoutX: 0, LayoutY: 0),
            new(subflowNodeId, WorkflowNodeKind.Subflow, AgentKey: null, AgentVersion: null, Script: null,
                OutputPorts: new[] { "Completed", "Failed", "Escalated" }, LayoutX: 250, LayoutY: 0,
                SubflowKey: subflowKey, SubflowVersion: subflowVersion),
        };

        var edges = new[]
        {
            new WorkflowEdge(startNodeId, "Completed", subflowNodeId, WorkflowEdge.DefaultInputPort, false, 0),
        };

        return new Workflow(
            Key: workflowKey,
            Version: 1,
            Name: workflowKey,
            MaxRoundsPerRound: 5,
            CreatedAtUtc: DateTime.UtcNow,
            Nodes: nodes,
            Edges: edges,
            Inputs: Array.Empty<WorkflowInput>());
    }

    private static Workflow BuildWorkflowWithReviewLoop(
        string workflowKey,
        Guid startNodeId,
        string startAgentKey,
        Guid reviewLoopNodeId,
        string subflowKey,
        int subflowVersion,
        int maxRounds)
    {
        var nodes = new List<WorkflowNode>
        {
            new(startNodeId, WorkflowNodeKind.Start, startAgentKey, AgentVersion: null, Script: null,
                OutputPorts: AllDecisionPorts, LayoutX: 0, LayoutY: 0),
            new(reviewLoopNodeId, WorkflowNodeKind.ReviewLoop, AgentKey: null, AgentVersion: null, Script: null,
                OutputPorts: new[] { "Approved", "Exhausted", "Failed" }, LayoutX: 250, LayoutY: 0,
                SubflowKey: subflowKey, SubflowVersion: subflowVersion, ReviewMaxRounds: maxRounds),
        };

        var edges = new[]
        {
            new WorkflowEdge(startNodeId, "Completed", reviewLoopNodeId, WorkflowEdge.DefaultInputPort, false, 0),
        };

        return new Workflow(
            Key: workflowKey,
            Version: 1,
            Name: workflowKey,
            MaxRoundsPerRound: 5,
            CreatedAtUtc: DateTime.UtcNow,
            Nodes: nodes,
            Edges: edges,
            Inputs: Array.Empty<WorkflowInput>());
    }

    private static async Task<IReadOnlyList<MassTransit.Testing.IPublishedMessage<T>>> WaitForPublishedAsync<T>(
        ITestHarness harness,
        int expectedCount,
        TimeSpan? timeout = null)
        where T : class
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTime.UtcNow < deadline)
        {
            var found = harness.Published.Select<T>().ToList();
            if (found.Count >= expectedCount)
            {
                return found;
            }

            await Task.Delay(25);
        }

        return harness.Published.Select<T>().ToList();
    }

    private static ITestHarness BuildHarness(
        Workflow workflow,
        IReadOnlyDictionary<string, int> agentVersions,
        IArtifactStore? artifactStore = null)
    {
        var provider = new ServiceCollection()
            .AddSingleton<IWorkflowRepository>(new FakeWorkflowRepository(workflow))
            .AddSingleton<IAgentConfigRepository>(new FakeAgentConfigRepository(agentVersions))
            .AddSingleton<IArtifactStore>(artifactStore ?? new StubArtifactStore(defaultJson: "{}"))
            .AddSingleton<IMemoryCache>(_ => new MemoryCache(new MemoryCacheOptions()))
            .AddSingleton<LogicNodeScriptHost>()
            .AddMassTransitTestHarness(x =>
            {
                x.AddSagaStateMachine<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
            })
            .BuildServiceProvider(true);

        return provider.GetRequiredService<ITestHarness>();
    }

    private sealed class StubArtifactStore : IArtifactStore
    {
        private readonly Func<Uri, string> payloadSelector;

        public StubArtifactStore(string defaultJson)
        {
            payloadSelector = _ => defaultJson;
        }

        public StubArtifactStore(Func<Uri, string> payloadSelector)
        {
            this.payloadSelector = payloadSelector;
        }

        public Task<ArtifactMetadata> GetMetadataAsync(Uri uri, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Stream> ReadAsync(Uri uri, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(payloadSelector(uri))));

        public Task<Uri> WriteAsync(Stream content, ArtifactMetadata metadata, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed record EdgeSpec(
        string From,
        RuntimeDecisionKind Decision,
        string To,
        bool RotatesRound,
        int SortOrder);

    private static EdgeSpec Edge(
        string from,
        RuntimeDecisionKind decision,
        string to,
        bool rotatesRound,
        int sortOrder = 0)
    {
        return new EdgeSpec(from, decision, to, rotatesRound, sortOrder);
    }

    private static Workflow BuildWorkflow(
        string key,
        int maxRounds,
        string startAgentKey,
        IReadOnlyList<EdgeSpec> edges,
        string? escalationAgentKey = null)
    {
        var agentKeys = new HashSet<string>(StringComparer.Ordinal) { startAgentKey };
        foreach (var edge in edges)
        {
            agentKeys.Add(edge.From);
            agentKeys.Add(edge.To);
        }

        if (escalationAgentKey is not null)
        {
            agentKeys.Add(escalationAgentKey);
        }

        var nodes = new List<WorkflowNode>();
        var nodeIds = new Dictionary<string, Guid>(StringComparer.Ordinal);

        foreach (var agentKey in agentKeys)
        {
            var id = Guid.NewGuid();
            nodeIds[agentKey] = id;

            WorkflowNodeKind kind;
            if (string.Equals(agentKey, startAgentKey, StringComparison.Ordinal))
            {
                kind = WorkflowNodeKind.Start;
            }
            else if (string.Equals(agentKey, escalationAgentKey, StringComparison.Ordinal))
            {
                kind = WorkflowNodeKind.Escalation;
            }
            else
            {
                kind = WorkflowNodeKind.Agent;
            }

            nodes.Add(new WorkflowNode(id, kind, agentKey, AgentVersion: null, Script: null,
                OutputPorts: AllDecisionPorts, LayoutX: 0, LayoutY: 0));
        }

        var workflowEdges = edges
            .Select((edge, index) => new WorkflowEdge(
                FromNodeId: nodeIds[edge.From],
                FromPort: edge.Decision.ToString(),
                ToNodeId: nodeIds[edge.To],
                ToPort: WorkflowEdge.DefaultInputPort,
                RotatesRound: edge.RotatesRound,
                SortOrder: edge.SortOrder == 0 ? index : edge.SortOrder))
            .ToArray();

        return new Workflow(
            Key: key,
            Version: 1,
            Name: key,
            MaxRoundsPerRound: maxRounds,
            CreatedAtUtc: DateTime.UtcNow,
            Nodes: nodes,
            Edges: workflowEdges,
            Inputs: Array.Empty<WorkflowInput>());
    }

    private static readonly IReadOnlyList<string> AllDecisionPorts =
        Enum.GetNames<AgentDecisionKind>();

    private static Guid NodeIdFor(Workflow workflow, string agentKey) =>
        workflow.Nodes.Single(n => string.Equals(n.AgentKey, agentKey, StringComparison.Ordinal)).Id;

    private static Task PublishStart(
        ITestHarness harness,
        Workflow workflow,
        Guid traceId,
        Guid roundId,
        int agentVersion = 1)
    {
        return harness.Bus.Publish(new AgentInvokeRequested(
            TraceId: traceId,
            RoundId: roundId,
            WorkflowKey: workflow.Key,
            WorkflowVersion: workflow.Version,
            NodeId: workflow.StartNode.Id,
            AgentKey: workflow.StartNode.AgentKey!,
            AgentVersion: agentVersion,
            InputRef: new Uri("file:///tmp/in.bin"),
            ContextInputs: new Dictionary<string, JsonElement>()));
    }

    private static AgentInvocationCompleted BuildCompletion(
        Workflow workflow,
        Guid traceId,
        Guid roundId,
        string agentKey,
        int agentVersion,
        AgentDecisionKind decision)
    {
        return new AgentInvocationCompleted(
            TraceId: traceId,
            RoundId: roundId,
            FromNodeId: NodeIdFor(workflow, agentKey),
            AgentKey: agentKey,
            AgentVersion: agentVersion,
            OutputPortName: AgentDecisionPorts.ToPortName(decision),
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
        public Task<Workflow> GetAsync(string key, int version, CancellationToken cancellationToken = default) =>
            Task.FromResult(workflow);

        public Task<Workflow?> GetLatestAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult<Workflow?>(workflow);

        public Task<IReadOnlyList<Workflow>> ListLatestAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Workflow>>(new[] { workflow });

        public Task<IReadOnlyList<Workflow>> ListVersionsAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Workflow>>(new[] { workflow });

        public Task<WorkflowEdge?> FindNextAsync(
            string key,
            int version,
            Guid fromNodeId,
            string outputPortName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(workflow.FindNext(fromNodeId, outputPortName));

        public Task<int> CreateNewVersionAsync(WorkflowDraft draft, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeAgentConfigRepository(IReadOnlyDictionary<string, int> versions)
        : IAgentConfigRepository
    {
        public Task<AgentConfig> GetAsync(string key, int version, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> CreateNewVersionAsync(string key, string configJson, string? createdBy, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> GetLatestVersionAsync(string key, CancellationToken cancellationToken = default) =>
            versions.TryGetValue(key, out var version)
                ? Task.FromResult(version)
                : throw new AgentConfigNotFoundException(key, 0);

        public Task<bool> RetireAsync(string key, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
