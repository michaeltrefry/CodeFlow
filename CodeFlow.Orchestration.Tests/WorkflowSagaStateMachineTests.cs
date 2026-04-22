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
