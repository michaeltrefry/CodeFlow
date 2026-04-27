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
using System.Text.Json.Nodes;

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
                Edge("evaluator", "Completed", "reviewer", rotatesRound: false)
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

            await harness.Bus.Publish(BuildCompletion(workflow, traceId, roundId, "evaluator", 1, "Completed"));

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
                .Which.Decision.Should().Be("Completed");
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task InitialAgentInvokeRequested_WithWorkflowContext_ShouldSeedSagaWorkflowInputsJson()
    {
        // Top-level traces enter via AgentInvokeRequested → Initially → ApplyInitialRequest.
        // The /api/traces endpoint runs the start node's InputScript and passes the script's
        // setWorkflow writes via the WorkflowContext field. ApplyInitialRequest must seed
        // saga.WorkflowInputsJson from that field so the start agent's prompt template can
        // resolve {{ global.* }} and downstream nodes inherit the seeded state — paralleling
        // the same behavior on subflow Start nodes (ApplyInitialSubflowAsync).
        var traceId = Guid.NewGuid();
        var roundId = Guid.NewGuid();
        var workflow = BuildWorkflow(
            key: "global-seed",
            maxRounds: 5,
            startAgentKey: "evaluator",
            edges: Array.Empty<EdgeSpec>());

        var harness = BuildHarness(workflow, new Dictionary<string, int>());
        await harness.Start();
        try
        {
            var workflowContext = new Dictionary<string, JsonElement>
            {
                ["seedKey"] = JsonDocument.Parse("\"seedValue\"").RootElement.Clone()
            };

            await harness.Bus.Publish(new AgentInvokeRequested(
                TraceId: traceId,
                RoundId: roundId,
                WorkflowKey: workflow.Key,
                WorkflowVersion: workflow.Version,
                NodeId: workflow.StartNode.Id,
                AgentKey: workflow.StartNode.AgentKey!,
                AgentVersion: 1,
                InputRef: new Uri("file:///tmp/in.bin"),
                ContextInputs: new Dictionary<string, JsonElement>(),
                WorkflowContext: workflowContext));

            var sagaHarness = harness.GetSagaStateMachineHarness<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
            var sagaInstance = await sagaHarness.Exists(traceId, x => x.Running);
            sagaInstance.Should().NotBeNull();

            var saga = sagaHarness.Sagas.Contains(sagaInstance!.Value);
            saga.Should().NotBeNull();
            saga!.WorkflowInputsJson.Should().NotBeNullOrWhiteSpace(
                "ApplyInitialRequest must seed WorkflowInputsJson from message.WorkflowContext");
            saga.WorkflowInputsJson!.Should().Contain("seedKey",
                "setWorkflow writes from a top-level Start input script (passed via WorkflowContext) must land in the saga");
            saga.WorkflowInputsJson.Should().Contain("seedValue");
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
                Edge("evaluator", "Completed", "reviewer", rotatesRound: true)
            ]);

        var harness = BuildHarness(workflow, new Dictionary<string, int> { ["reviewer"] = 2 });

        await harness.Start();
        try
        {
            await PublishStart(harness, workflow, traceId, roundId);

            var sagaHarness = harness.GetSagaStateMachineHarness<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
            await sagaHarness.Exists(traceId, x => x.Running);

            await harness.Bus.Publish(BuildCompletion(workflow, traceId, roundId, "evaluator", 1, "Completed"));

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
                Edge("reviewer", "Rejected", "evaluator", rotatesRound: false),
                Edge("evaluator", "Completed", "reviewer", rotatesRound: false)
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

            await harness.Bus.Publish(BuildCompletion(workflow, traceId, roundId, "evaluator", 1, "Completed"));

            await sagaHarness.Exists(traceId, s => s.Running);
            SpinWaitUntil(() => sagaHarness.Sagas.Contains(traceId)?.RoundCount == 1);

            await harness.Bus.Publish(BuildCompletion(workflow, traceId, roundId, "reviewer", 1, "Rejected"));

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
                Edge("evaluator", "Completed", "reviewer", rotatesRound: false)
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
                workflow, traceId, staleRoundId, "evaluator", 1, "Completed"));

            // Give the saga a moment to process (or reject) the message.
            await Task.Delay(200);

            var saga = sagaHarness.Sagas.Contains(traceId)!;
            saga.CurrentAgentKey.Should().Be("evaluator",
                "stale-round completion must not advance the saga to the next agent");
            saga.GetDecisionHistory().Should().BeEmpty(
                "stale-round completion must not be recorded in decision history");

            // Sanity: a completion with the correct RoundId still advances the saga.
            await harness.Bus.Publish(BuildCompletion(
                workflow, traceId, currentRoundId, "evaluator", 1, "Completed"));
            SpinWaitUntil(() => sagaHarness.Sagas.Contains(traceId)?.CurrentAgentKey == "reviewer");
            sagaHarness.Sagas.Contains(traceId)!.GetDecisionHistory().Should().ContainSingle();
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task UnmappedDecision_ShouldTerminateCompleted()
    {
        // Slice 5 / new port model: any non-"Failed" port name with no outgoing edge terminates
        // the saga cleanly as Completed. The terminal port name is preserved on
        // saga.LastEffectivePort. Only the implicit "Failed" port produces a Failed terminal.
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

            await harness.Bus.Publish(BuildCompletion(workflow, traceId, roundId, "evaluator", 1, "Rejected"));

            await sagaHarness.Exists(traceId, s => s.Completed);
            var saga = sagaHarness.Sagas.Contains(traceId)!;
            saga.CurrentState.Should().Be(nameof(WorkflowSagaStateMachine.Completed));
            saga.PendingTransition.Should().BeNull("state machine clears the pending flag after transitioning");
            saga.LastEffectivePort.Should().Be("Rejected",
                "the terminal port name is preserved verbatim so it can ride up to a parent saga");
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

            await harness.Bus.Publish(BuildCompletion(workflow, traceId, roundId, "publisher", 1, "Completed"));

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
    public async Task RoundCountExceeded_ShouldFail()
    {
        var traceId = Guid.NewGuid();
        var roundId = Guid.NewGuid();
        var workflow = BuildWorkflow(
            key: "capfail",
            maxRounds: 3,
            startAgentKey: "looper",
            edges:
            [
                Edge("looper", "Rejected", "looper", rotatesRound: false)
            ]);

        var harness = BuildHarness(workflow, new Dictionary<string, int> { ["looper"] = 1 });
        await harness.Start();
        try
        {
            await PublishStart(harness, workflow, traceId, roundId);

            var sagaHarness = harness.GetSagaStateMachineHarness<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
            await sagaHarness.Exists(traceId, s => s.Running);

            await harness.Bus.Publish(BuildCompletion(workflow, traceId, roundId, "looper", 1, "Rejected"));
            SpinWaitUntil(() => sagaHarness.Sagas.Contains(traceId)?.RoundCount == 1);
            await harness.Bus.Publish(BuildCompletion(workflow, traceId, roundId, "looper", 1, "Rejected"));
            SpinWaitUntil(() => sagaHarness.Sagas.Contains(traceId)?.RoundCount == 2);
            await harness.Bus.Publish(BuildCompletion(workflow, traceId, roundId, "looper", 1, "Rejected"));

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
            logicOutputPorts: new[] { "Failed" },
            downstreamAgents: new[] { "fallback" },
            classifierToLogicPort: "Completed",
            logicPortToAgent: new Dictionary<string, string>
            {
                ["Failed"] = "fallback"
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
            setNodePath(output.verdict === 'ok' ? 'Accept' : 'Reject');
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
            setNodePath(output.decision === 'Rejected' ? 'Revise' : 'Accept');
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
            prior.push({ q: output.question, a: output.answer });
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
            if (output.decision === 'Approved') { setNodePath('Answer'); }
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
            if (output.decision === 'Answered') { setNodePath('Answer'); }
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

    [Fact]
    public async Task ScriptedAgent_SetOutput_SubstitutesDownstreamInputRefAndPreservesOriginal()
    {
        // Agent emits a raw JSON submission; the routing script builds a markdown summary and
        // calls setOutput(md). The next dispatched agent must receive the markdown as its
        // InputRef artifact; the original submission must still be in the store and referenced
        // from the decision record prior to the override.
        var traceId = Guid.NewGuid();
        var roundId = Guid.NewGuid();
        const string script = """
            var md = '# Summary\n- answer: ' + output.answer;
            setOutput(md);
            setNodePath('Completed');
            """;
        var workflow = BuildWorkflowWithScriptedSource(
            key: "scripted-setOutput",
            sourceAgentKey: "interviewer",
            sourceScript: script,
            sourceOutputPorts: new[] { "Completed" },
            downstream: new Dictionary<string, string>
            {
                ["Completed"] = "publisher"
            });

        var originalOutputRef = new Uri("file:///tmp/interviewer-out.bin");
        var artifactStore = new RecordingArtifactStore();
        artifactStore.SeedRead(originalOutputRef, """{"answer":"forty-two"}""");

        var harness = BuildHarness(workflow, new Dictionary<string, int>
        {
            ["publisher"] = 1
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
                OutputRef: originalOutputRef,
                DecisionPayload: null,
                Duration: TimeSpan.FromMilliseconds(1),
                TokenUsage: new Contracts.TokenUsage(0, 0, 0)));

            SpinWaitUntil(() => harness.Published.Select<AgentInvokeRequested>()
                .Any(x => x.Context.Message.AgentKey == "publisher"));

            var downstream = harness.Published.Select<AgentInvokeRequested>()
                .Single(x => x.Context.Message.AgentKey == "publisher");

            // Downstream agent's InputRef must point at the newly written override artifact,
            // not the original submission.
            var downstreamInputRef = downstream.Context.Message.InputRef!;
            downstreamInputRef.Should().NotBe(originalOutputRef);
            artifactStore.ReadWrittenContent(downstreamInputRef)
                .Should().Be("# Summary\n- answer: forty-two");

            // Original submission is still readable — the script host must never mutate it.
            artifactStore.SeededReads.Should().ContainKey(originalOutputRef);

            // DecisionRecord for the source node carries the overridden OutputRef so the trace
            // UI renders the rendered markdown rather than the raw submission.
            var saga = sagaHarness.Sagas.Contains(traceId)!;
            var decisions = saga.GetDecisionHistory();
            var interviewerDecision = decisions
                .Single(d => d.AgentKey == "interviewer" && d.RoundId == roundId);
            interviewerDecision.OutputRef.Should().Be(downstreamInputRef.ToString());
            interviewerDecision.OutputRef.Should().NotBe(originalOutputRef.ToString());
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task ScriptedAgent_SetInput_SubstitutesDispatchInputRefAndPreservesOriginal()
    {
        // Downstream agent carries an InputScript. Before dispatch, the saga evaluates the script;
        // setInput(md) writes a new artifact and the dispatched AgentInvokeRequested carries the
        // override as InputRef. The original upstream artifact is untouched in the store.
        var traceId = Guid.NewGuid();
        var roundId = Guid.NewGuid();

        var startId = Guid.NewGuid();
        var downstreamId = Guid.NewGuid();
        const string inputScript = """
            setInput('# Briefing\n- topic: ' + input.topic);
            """;
        var workflow = new Workflow(
            Key: "scripted-setInput",
            Version: 1,
            Name: "scripted-setInput",
            MaxRoundsPerRound: 5,
            CreatedAtUtc: DateTime.UtcNow,
            Nodes: new[]
            {
                new WorkflowNode(startId, WorkflowNodeKind.Start, "interviewer", 1,
                    OutputScript: null, OutputPorts: AllDecisionPorts, LayoutX: 0, LayoutY: 0),
                new WorkflowNode(downstreamId, WorkflowNodeKind.Agent, "publisher", 1,
                    OutputScript: null, OutputPorts: AllDecisionPorts, LayoutX: 500, LayoutY: 0,
                    InputScript: inputScript)
            },
            Edges: new[]
            {
                new WorkflowEdge(startId, "Completed", downstreamId,
                    WorkflowEdge.DefaultInputPort, false, 0)
            },
            Inputs: Array.Empty<WorkflowInput>());

        var originalOutputRef = new Uri("file:///tmp/interviewer-out.bin");
        var artifactStore = new RecordingArtifactStore();
        artifactStore.SeedRead(originalOutputRef, """{"topic":"shipping the feature"}""");

        var harness = BuildHarness(workflow, new Dictionary<string, int>
        {
            ["publisher"] = 1
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
                FromNodeId: startId,
                AgentKey: "interviewer",
                AgentVersion: 1,
                OutputPortName: "Completed",
                OutputRef: originalOutputRef,
                DecisionPayload: null,
                Duration: TimeSpan.FromMilliseconds(1),
                TokenUsage: new Contracts.TokenUsage(0, 0, 0)));

            SpinWaitUntil(() => harness.Published.Select<AgentInvokeRequested>()
                .Any(x => x.Context.Message.AgentKey == "publisher"));

            var downstream = harness.Published.Select<AgentInvokeRequested>()
                .Single(x => x.Context.Message.AgentKey == "publisher");

            var downstreamInputRef = downstream.Context.Message.InputRef!;
            downstreamInputRef.Should().NotBe(originalOutputRef,
                "InputScript's setInput() must substitute the downstream agent's InputRef.");
            artifactStore.ReadWrittenContent(downstreamInputRef)
                .Should().Be("# Briefing\n- topic: shipping the feature");

            // Original upstream artifact is still readable.
            artifactStore.SeededReads.Should().ContainKey(originalOutputRef);

            // Input-script evaluation was logged to the saga's LogicEvaluationHistory.
            var saga = sagaHarness.Sagas.Contains(traceId)!;
            var logicHistory = saga.GetLogicEvaluationHistory().ToList();
            logicHistory.Should().Contain(r =>
                r.NodeId == downstreamId && r.FailureKind == null);
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task ScriptedAgent_SetInput_BudgetExceeded_FailsSaga()
    {
        // 1 MiB cap — a setInput() payload above the cap must fail the saga with
        // InputOverrideBudgetExceeded, rather than dispatching to the downstream agent.
        var traceId = Guid.NewGuid();
        var roundId = Guid.NewGuid();

        var startId = Guid.NewGuid();
        var downstreamId = Guid.NewGuid();
        // 1.2 MiB — over the 1,048,576-char budget. Built with String.repeat to avoid the
        // 4 MB Jint memory limit a concat-loop would trip.
        const string inputScript = """
            setInput('x'.repeat(1200000));
            """;
        var workflow = new Workflow(
            Key: "scripted-setInput-budget",
            Version: 1,
            Name: "scripted-setInput-budget",
            MaxRoundsPerRound: 5,
            CreatedAtUtc: DateTime.UtcNow,
            Nodes: new[]
            {
                new WorkflowNode(startId, WorkflowNodeKind.Start, "interviewer", 1,
                    OutputScript: null, OutputPorts: AllDecisionPorts, LayoutX: 0, LayoutY: 0),
                new WorkflowNode(downstreamId, WorkflowNodeKind.Agent, "publisher", 1,
                    OutputScript: null, OutputPorts: AllDecisionPorts, LayoutX: 500, LayoutY: 0,
                    InputScript: inputScript)
            },
            Edges: new[]
            {
                new WorkflowEdge(startId, "Completed", downstreamId,
                    WorkflowEdge.DefaultInputPort, false, 0)
            },
            Inputs: Array.Empty<WorkflowInput>());

        var originalOutputRef = new Uri("file:///tmp/interviewer-out.bin");
        var artifactStore = new RecordingArtifactStore();
        artifactStore.SeedRead(originalOutputRef, """{"topic":"x"}""");

        var harness = BuildHarness(workflow, new Dictionary<string, int>
        {
            ["publisher"] = 1
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
                FromNodeId: startId,
                AgentKey: "interviewer",
                AgentVersion: 1,
                OutputPortName: "Completed",
                OutputRef: originalOutputRef,
                DecisionPayload: null,
                Duration: TimeSpan.FromMilliseconds(1),
                TokenUsage: new Contracts.TokenUsage(0, 0, 0)));

            await sagaHarness.Exists(traceId, s => s.Failed);

            // Downstream agent must never have been dispatched.
            harness.Published.Select<AgentInvokeRequested>()
                .Any(x => x.Context.Message.AgentKey == "publisher")
                .Should().BeFalse();

            var saga = sagaHarness.Sagas.Contains(traceId)!;
            saga.FailureReason.Should().NotBeNull().And.Subject.Should().Contain("InputOverrideBudgetExceeded");

            var logicHistory = saga.GetLogicEvaluationHistory().ToList();
            logicHistory.Should().Contain(r =>
                r.NodeId == downstreamId
                && r.FailureKind == nameof(LogicNodeFailureKind.InputOverrideBudgetExceeded));
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task DecisionOutputTemplate_RendersForExactPortMatch_AndSubstitutesDownstreamInput()
    {
        // Source agent declares a per-decision template for "Approved". On decision completion the
        // saga renders the template server-side, writes the rendered content as an override
        // artifact, and the downstream agent receives the rendered content as its InputRef.
        var traceId = Guid.NewGuid();
        var roundId = Guid.NewGuid();
        var workflow = BuildWorkflowWithScriptedSource(
            key: "decision-template-exact",
            sourceAgentKey: "evaluator",
            sourceScript: null,
            sourceOutputPorts: new[] { "Approved" },
            downstream: new Dictionary<string, string>
            {
                ["Approved"] = "publisher"
            });

        var originalOutputRef = new Uri("file:///tmp/evaluator-out.json");
        var artifactStore = new RecordingArtifactStore();
        artifactStore.SeedRead(originalOutputRef, """{"headline":"Ready to ship"}""");

        var agentConfigs = new Dictionary<string, AgentConfig>
        {
            ["evaluator"] = BuildAgentConfig("evaluator", 1, new Dictionary<string, string>
            {
                ["Approved"] = "APPROVED: {{ output.headline }} (port={{ outputPortName }})"
            })
        };

        var harness = BuildHarness(workflow,
            new Dictionary<string, int> { ["publisher"] = 1 },
            artifactStore,
            agentConfigs);
        await harness.Start();
        try
        {
            await PublishStart(harness, workflow, traceId, roundId);
            var sagaHarness = harness.GetSagaStateMachineHarness<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
            await sagaHarness.Exists(traceId, s => s.Running);

            await harness.Bus.Publish(new AgentInvocationCompleted(
                TraceId: traceId,
                RoundId: roundId,
                FromNodeId: NodeIdFor(workflow, "evaluator"),
                AgentKey: "evaluator",
                AgentVersion: 1,
                OutputPortName: "Approved",
                OutputRef: originalOutputRef,
                DecisionPayload: null,
                Duration: TimeSpan.FromMilliseconds(1),
                TokenUsage: new Contracts.TokenUsage(0, 0, 0)));

            SpinWaitUntil(() => harness.Published.Select<AgentInvokeRequested>()
                .Any(x => x.Context.Message.AgentKey == "publisher"));

            var downstream = harness.Published.Select<AgentInvokeRequested>()
                .Single(x => x.Context.Message.AgentKey == "publisher");
            var downstreamInputRef = downstream.Context.Message.InputRef!;
            downstreamInputRef.Should().NotBe(originalOutputRef);

            artifactStore.ReadWrittenContent(downstreamInputRef)
                .Should().Be("APPROVED: Ready to ship (port=Approved)");

            // Original submission is untouched; decision record points at the rendered override.
            artifactStore.SeededReads.Should().ContainKey(originalOutputRef);
            var saga = sagaHarness.Sagas.Contains(traceId)!;
            var decisions = saga.GetDecisionHistory();
            decisions.Single(d => d.AgentKey == "evaluator" && d.RoundId == roundId)
                .OutputRef.Should().Be(downstreamInputRef.ToString());
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task DecisionOutputTemplate_FallsBackToWildcard_WhenNoExactMatch()
    {
        var traceId = Guid.NewGuid();
        var roundId = Guid.NewGuid();
        var workflow = BuildWorkflowWithScriptedSource(
            key: "decision-template-wildcard",
            sourceAgentKey: "evaluator",
            sourceScript: null,
            sourceOutputPorts: new[] { "Approved" },
            downstream: new Dictionary<string, string>
            {
                ["Approved"] = "publisher"
            });

        var originalOutputRef = new Uri("file:///tmp/evaluator-out.json");
        var artifactStore = new RecordingArtifactStore();
        artifactStore.SeedRead(originalOutputRef, """{"note":"fine"}""");

        var agentConfigs = new Dictionary<string, AgentConfig>
        {
            ["evaluator"] = BuildAgentConfig("evaluator", 1, new Dictionary<string, string>
            {
                ["*"] = "[{{ decision }}] wildcard-{{ output.note }}"
            })
        };

        var harness = BuildHarness(workflow,
            new Dictionary<string, int> { ["publisher"] = 1 },
            artifactStore,
            agentConfigs);
        await harness.Start();
        try
        {
            await PublishStart(harness, workflow, traceId, roundId);
            var sagaHarness = harness.GetSagaStateMachineHarness<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
            await sagaHarness.Exists(traceId, s => s.Running);

            await harness.Bus.Publish(new AgentInvocationCompleted(
                TraceId: traceId,
                RoundId: roundId,
                FromNodeId: NodeIdFor(workflow, "evaluator"),
                AgentKey: "evaluator",
                AgentVersion: 1,
                OutputPortName: "Approved",
                OutputRef: originalOutputRef,
                DecisionPayload: null,
                Duration: TimeSpan.FromMilliseconds(1),
                TokenUsage: new Contracts.TokenUsage(0, 0, 0)));

            SpinWaitUntil(() => harness.Published.Select<AgentInvokeRequested>()
                .Any(x => x.Context.Message.AgentKey == "publisher"));

            var downstream = harness.Published.Select<AgentInvokeRequested>()
                .Single(x => x.Context.Message.AgentKey == "publisher");
            var downstreamInputRef = downstream.Context.Message.InputRef!;
            artifactStore.ReadWrittenContent(downstreamInputRef)
                .Should().Be("[Approved] wildcard-fine");
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task DecisionOutputTemplate_NoMatchLeavesOriginalOutputRefIntact()
    {
        // Agent has some templates but none match the submitted decision and no wildcard — the
        // saga must leave the OutputRef pointing at the original submission.
        var traceId = Guid.NewGuid();
        var roundId = Guid.NewGuid();
        var workflow = BuildWorkflowWithScriptedSource(
            key: "decision-template-none",
            sourceAgentKey: "evaluator",
            sourceScript: null,
            sourceOutputPorts: new[] { "Approved" },
            downstream: new Dictionary<string, string>
            {
                ["Approved"] = "publisher"
            });

        var originalOutputRef = new Uri("file:///tmp/evaluator-out.bin");
        var artifactStore = new RecordingArtifactStore();
        artifactStore.SeedRead(originalOutputRef, "raw submission");

        var agentConfigs = new Dictionary<string, AgentConfig>
        {
            ["evaluator"] = BuildAgentConfig("evaluator", 1, new Dictionary<string, string>
            {
                ["Rejected"] = "not used"
            })
        };

        var harness = BuildHarness(workflow,
            new Dictionary<string, int> { ["publisher"] = 1 },
            artifactStore,
            agentConfigs);
        await harness.Start();
        try
        {
            await PublishStart(harness, workflow, traceId, roundId);
            var sagaHarness = harness.GetSagaStateMachineHarness<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
            await sagaHarness.Exists(traceId, s => s.Running);

            await harness.Bus.Publish(new AgentInvocationCompleted(
                TraceId: traceId,
                RoundId: roundId,
                FromNodeId: NodeIdFor(workflow, "evaluator"),
                AgentKey: "evaluator",
                AgentVersion: 1,
                OutputPortName: "Approved",
                OutputRef: originalOutputRef,
                DecisionPayload: null,
                Duration: TimeSpan.FromMilliseconds(1),
                TokenUsage: new Contracts.TokenUsage(0, 0, 0)));

            SpinWaitUntil(() => harness.Published.Select<AgentInvokeRequested>()
                .Any(x => x.Context.Message.AgentKey == "publisher"));

            var downstream = harness.Published.Select<AgentInvokeRequested>()
                .Single(x => x.Context.Message.AgentKey == "publisher");
            downstream.Context.Message.InputRef.Should().Be(originalOutputRef);
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task DecisionOutputTemplate_RenderFailure_TransitionsSagaToFailed()
    {
        var traceId = Guid.NewGuid();
        var roundId = Guid.NewGuid();
        var workflow = BuildWorkflowWithScriptedSource(
            key: "decision-template-fail",
            sourceAgentKey: "evaluator",
            sourceScript: null,
            sourceOutputPorts: new[] { "Approved" },
            downstream: new Dictionary<string, string>
            {
                ["Approved"] = "publisher"
            });

        var originalOutputRef = new Uri("file:///tmp/evaluator-out.json");
        var artifactStore = new RecordingArtifactStore();
        artifactStore.SeedRead(originalOutputRef, """{"headline":"x"}""");

        var agentConfigs = new Dictionary<string, AgentConfig>
        {
            // Malformed template — unterminated `{{ if` raises PromptTemplateException during render.
            ["evaluator"] = BuildAgentConfig("evaluator", 1, new Dictionary<string, string>
            {
                ["Approved"] = "{{ if output.missing"
            })
        };

        var harness = BuildHarness(workflow,
            new Dictionary<string, int> { ["publisher"] = 1 },
            artifactStore,
            agentConfigs);
        await harness.Start();
        try
        {
            await PublishStart(harness, workflow, traceId, roundId);
            var sagaHarness = harness.GetSagaStateMachineHarness<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
            await sagaHarness.Exists(traceId, s => s.Running);

            await harness.Bus.Publish(new AgentInvocationCompleted(
                TraceId: traceId,
                RoundId: roundId,
                FromNodeId: NodeIdFor(workflow, "evaluator"),
                AgentKey: "evaluator",
                AgentVersion: 1,
                OutputPortName: "Approved",
                OutputRef: originalOutputRef,
                DecisionPayload: null,
                Duration: TimeSpan.FromMilliseconds(1),
                TokenUsage: new Contracts.TokenUsage(0, 0, 0)));

            await sagaHarness.Exists(traceId, s => s.Failed);

            var saga = sagaHarness.Sagas.Contains(traceId)!;
            saga.FailureReason.Should().Contain("Decision output template failed");

            // Downstream agent must not have been invoked on a template failure.
            harness.Published.Select<AgentInvokeRequested>()
                .Any(x => x.Context.Message.AgentKey == "publisher")
                .Should().BeFalse();
        }
        finally
        {
            await harness.Stop();
        }
    }

    private static AgentConfig BuildAgentConfig(
        string key,
        int version,
        IReadOnlyDictionary<string, string> decisionOutputTemplates)
    {
        var configuration = new Runtime.AgentInvocationConfiguration(
            Provider: "openai",
            Model: "gpt-test",
            DecisionOutputTemplates: decisionOutputTemplates);
        return new AgentConfig(
            Key: key,
            Version: version,
            Kind: AgentKind.Agent,
            Configuration: configuration,
            ConfigJson: "{}",
            CreatedAtUtc: DateTime.UtcNow,
            CreatedBy: null);
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
                Edge("reviewer", "Failed", "reviewer", rotatesRound: false)
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
                OutputPortName: "Failed",
                OutputRef: new Uri("file:///tmp/reviewer-out.bin"),
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

            await harness.Bus.Publish(BuildCompletion(workflow, traceId, roundId, "kickoff", 1, "Completed"));

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
            dispatched.WorkflowContext.Should().BeEmpty(
                "no setWorkflow writes have occurred and no API caller seeded global at start");

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

            await harness.Bus.Publish(BuildCompletion(workflow, traceId, roundId, "kickoff", 1, "Completed"));

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
        // node terminates the parent in the Completed state. The child's final WorkflowContext is
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
            await harness.Bus.Publish(BuildCompletion(workflow, traceId, parentRoundId, "kickoff", 1, "Completed"));
            await WaitForPublishedAsync<SubflowInvokeRequested>(harness, expectedCount: 1);

            // Sanity: parent is now sitting on the Subflow node.
            var parent = sagaHarness.Sagas.Contains(traceId)!;
            parent.CurrentNodeId.Should().Be(subflowNodeId);
            parent.CurrentRoundId.Should().Be(parentRoundId);

            // Synthesize the child's completion with a WorkflowContext that should propagate.
            var childWorkflowBag = new Dictionary<string, JsonElement>
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
                WorkflowContext: childWorkflowBag));

            await sagaHarness.Exists(traceId, x => x.Completed);

            var resumed = sagaHarness.Sagas.Contains(traceId)!;
            resumed.CurrentState.Should().Be(nameof(WorkflowSagaStateMachine.Completed));
            resumed.WorkflowInputsJson.Should().NotBeNullOrWhiteSpace();
            resumed.WorkflowInputsJson!.Should().Contain("resolvedSpec");
            resumed.WorkflowInputsJson.Should().Contain("fromChild");

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
            await harness.Bus.Publish(BuildCompletion(workflow, traceId, parentRoundId, "kickoff", 1, "Completed"));
            await WaitForPublishedAsync<SubflowInvokeRequested>(harness, expectedCount: 1);

            await harness.Bus.Publish(new SubflowCompleted(
                ParentTraceId: traceId,
                ParentNodeId: subflowNodeId,
                ParentRoundId: parentRoundId,
                ChildTraceId: Guid.NewGuid(),
                OutputPortName: "Failed",
                OutputRef: new Uri("file:///tmp/child-failed.bin"),
                WorkflowContext: new Dictionary<string, JsonElement>()));

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
            await harness.Bus.Publish(BuildCompletion(workflow, traceId, parentRoundId, "kickoff", 1, "Completed"));
            await WaitForPublishedAsync<SubflowInvokeRequested>(harness, expectedCount: 1);

            await harness.Bus.Publish(new SubflowCompleted(
                ParentTraceId: traceId,
                ParentNodeId: subflowNodeId,
                ParentRoundId: Guid.NewGuid(), // intentionally wrong round
                ChildTraceId: Guid.NewGuid(),
                OutputPortName: "Completed",
                OutputRef: new Uri("file:///tmp/stale.bin"),
                WorkflowContext: new Dictionary<string, JsonElement>
                {
                    ["shouldNotMerge"] = JsonDocument.Parse("\"true\"").RootElement.Clone(),
                }));

            // Give the bus a moment to process; saga must remain in Running.
            await Task.Delay(500);
            var saga = sagaHarness.Sagas.Contains(traceId)!;
            saga.CurrentState.Should().Be(nameof(WorkflowSagaStateMachine.Running));
            (saga.WorkflowInputsJson ?? string.Empty).Should().NotContain("shouldNotMerge",
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
        // Slice 4: a child's setWorkflow writes during round N must be visible to round N+1 (via
        // WorkflowContext on the next SubflowInvokeRequested) and must survive into the parent's
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
            await harness.Bus.Publish(BuildCompletion(workflow, traceId, parentRoundId, "kickoff", 1, "Completed"));

            var round1Requests = await WaitForPublishedAsync<SubflowInvokeRequested>(harness, expectedCount: 1);
            round1Requests.Should().HaveCount(1);
            var round1 = round1Requests[0].Context.Message;
            round1.ReviewRound.Should().Be(1);
            round1.ReviewMaxRounds.Should().Be(2);

            // Child round 1 finishes Rejected with setWorkflow('counter', 1).
            var round1WorkflowBag = new Dictionary<string, JsonElement>
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
                WorkflowContext: round1WorkflowBag,
                Decision: "Rejected",
                ReviewRound: 1,
                TerminalPort: "Rejected"));

            // Parent must not terminate — it should spawn round 2 instead.
            var round2Requests = await WaitForPublishedAsync<SubflowInvokeRequested>(harness, expectedCount: 2);
            round2Requests.Should().HaveCount(2);
            var round2 = round2Requests[1].Context.Message;
            round2.ReviewRound.Should().Be(2, "Rejected with rounds remaining advances the round counter");
            round2.ReviewMaxRounds.Should().Be(2);
            round2.InputRef.Should().Be(new Uri("file:///tmp/round1-out.bin"),
                "round N+1 input = round N's output artifact");
            round2.WorkflowContext.Should().ContainKey("counter");
            round2.WorkflowContext["counter"].GetInt32().Should().Be(1,
                "round 2 must see round 1's setWorkflow writes through its WorkflowContext snapshot");

            // Child round 2 approves with an additional global write.
            var round2WorkflowBag = new Dictionary<string, JsonElement>
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
                WorkflowContext: round2WorkflowBag,
                Decision: "Approved",
                ReviewRound: 2));

            // Approved port has no outgoing edge in this workflow, so the parent falls through
            // to the Completed terminal state.
            await sagaHarness.Exists(traceId, x => x.Completed);

            var resumed = sagaHarness.Sagas.Contains(traceId)!;
            resumed.CurrentState.Should().Be(nameof(WorkflowSagaStateMachine.Completed));
            resumed.WorkflowInputsJson.Should().NotBeNullOrWhiteSpace();
            resumed.WorkflowInputsJson!.Should().Contain("\"counter\":2",
                "the parent's final global must reflect the last round's write");
            resumed.WorkflowInputsJson.Should().Contain("done",
                "keys added only in the final round must also be merged up");
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Theory]
    [InlineData("Approved")]
    [InlineData("Completed")]
    public async Task ReviewLoopCompleted_NonLoopDecisionOnRound1_ShouldExitVerbatimPort(
        string childTerminalPort)
    {
        // Slice 5 / new port model: any terminal port name that doesn't match the configured
        // LoopDecision propagates verbatim to the parent's ReviewLoop node — there is no
        // Approved/Completed → Approved enum mapping anymore. With no downstream edge from
        // either "Approved" or "Completed" on the parent's ReviewLoop node, the parent falls
        // through to a clean Completed terminal (since neither port name is the implicit
        // "Failed" sink).
        var traceId = Guid.NewGuid();
        var parentRoundId = Guid.NewGuid();
        var startNodeId = Guid.NewGuid();
        var reviewLoopNodeId = Guid.NewGuid();

        var workflow = BuildWorkflowWithReviewLoop(
            "rl-nonloop-round1", startNodeId, "kickoff", reviewLoopNodeId,
            subflowKey: "critique-revise", subflowVersion: 1, maxRounds: 3);

        var harness = BuildHarness(workflow, new Dictionary<string, int>());
        await harness.Start();
        try
        {
            await PublishStart(harness, workflow, traceId, parentRoundId);
            var sagaHarness = harness.GetSagaStateMachineHarness<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
            await sagaHarness.Exists(traceId, x => x.Running);

            await harness.Bus.Publish(BuildCompletion(workflow, traceId, parentRoundId, "kickoff", 1, "Completed"));
            var round1 = (await WaitForPublishedAsync<SubflowInvokeRequested>(harness, expectedCount: 1))[0].Context.Message;

            await harness.Bus.Publish(new SubflowCompleted(
                ParentTraceId: traceId,
                ParentNodeId: reviewLoopNodeId,
                ParentRoundId: parentRoundId,
                ChildTraceId: round1.ChildTraceId,
                OutputPortName: childTerminalPort,
                OutputRef: new Uri("file:///tmp/round1-out.bin"),
                WorkflowContext: new Dictionary<string, JsonElement>(),
                Decision: childTerminalPort,
                ReviewRound: 1,
                TerminalPort: childTerminalPort));

            await sagaHarness.Exists(traceId, x => x.Completed);

            // Exactly one round spawned — non-loopDecision terminal exits the loop immediately.
            var allRequests = await WaitForPublishedAsync<SubflowInvokeRequested>(harness, expectedCount: 1);
            allRequests.Should().HaveCount(1);

            var resumed = sagaHarness.Sagas.Contains(traceId)!;
            resumed.GetDecisionHistory().Should()
                .Contain(d => d.NodeId == reviewLoopNodeId && d.OutputPortName == childTerminalPort,
                    "synthetic parent decision for the ReviewLoop records the verbatim child terminal port");
        }
        finally { await harness.Stop(); }
    }

    [Fact]
    public async Task ReviewLoopCompleted_RejectOnEveryRound_ShouldExitExhaustedPort()
    {
        // Slice 10 scenario 4 (post-Slice-5 port-model redesign): with MaxRounds=2, a Rejected on
        // round 1 spawns round 2; a Rejected on round 2 has no rounds left and exits via the
        // synthesized Exhausted port. Per the new port model, Exhausted is just a port name —
        // unwired, the saga terminates cleanly (Completed) with `LastEffectivePort = "Exhausted"`
        // so authors can read the terminal port from the trace. Authors who want round-budget
        // exhaustion to fail-loud must wire an Exhausted → Failed edge explicitly.
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

            await harness.Bus.Publish(BuildCompletion(workflow, traceId, parentRoundId, "kickoff", 1, "Completed"));
            var round1 = (await WaitForPublishedAsync<SubflowInvokeRequested>(harness, expectedCount: 1))[0].Context.Message;

            // Round 1: child's terminal port = "Rejected" matches loopDecision → next round.
            await harness.Bus.Publish(new SubflowCompleted(
                ParentTraceId: traceId, ParentNodeId: reviewLoopNodeId, ParentRoundId: parentRoundId,
                ChildTraceId: round1.ChildTraceId, OutputPortName: "Rejected",
                OutputRef: new Uri("file:///tmp/r1-out.bin"),
                WorkflowContext: new Dictionary<string, JsonElement>(),
                Decision: "Rejected", ReviewRound: 1, TerminalPort: "Rejected"));

            var round2 = (await WaitForPublishedAsync<SubflowInvokeRequested>(harness, expectedCount: 2))[1].Context.Message;
            round2.ReviewRound.Should().Be(2);

            // Round 2 (last): Rejected → Exhausted port (no outgoing edge → Failed terminal).
            await harness.Bus.Publish(new SubflowCompleted(
                ParentTraceId: traceId, ParentNodeId: reviewLoopNodeId, ParentRoundId: parentRoundId,
                ChildTraceId: round2.ChildTraceId, OutputPortName: "Rejected",
                OutputRef: new Uri("file:///tmp/r2-out.bin"),
                WorkflowContext: new Dictionary<string, JsonElement>(),
                Decision: "Rejected", ReviewRound: 2, TerminalPort: "Rejected"));

            await sagaHarness.Exists(traceId, x => x.Completed);

            var resumed = sagaHarness.Sagas.Contains(traceId)!;
            resumed.LastEffectivePort.Should().Be("Exhausted",
                "the synthesized Exhausted port name is preserved on the terminal saga state");

            resumed.GetDecisionHistory().Should()
                .Contain(d => d.NodeId == reviewLoopNodeId && d.OutputPortName == "Exhausted");
        }
        finally { await harness.Stop(); }
    }

    [Fact]
    public async Task ReviewLoopCompleted_RejectionHistoryEnabled_AccumulatesArtifactBodyAcrossRounds()
    {
        // P3: when the parent ReviewLoop has rejection history enabled, each non-final Rejected
        // round appends the loop-decision artifact body to the framework-managed
        // `__loop.rejectionHistory` workflow variable. The final Approved round exits via the
        // Approved port and the accumulated history rides up on the parent saga's workflow bag
        // (so a downstream node can still read it after the loop completes).
        var traceId = Guid.NewGuid();
        var parentRoundId = Guid.NewGuid();
        var startNodeId = Guid.NewGuid();
        var reviewLoopNodeId = Guid.NewGuid();

        var workflow = BuildWorkflowWithReviewLoop(
            "rl-rejection-history", startNodeId, "kickoff", reviewLoopNodeId,
            subflowKey: "critique-revise", subflowVersion: 1, maxRounds: 3,
            rejectionHistory: new RejectionHistoryConfig(Enabled: true));

        var round1Body = "## Findings\n- missing API in section 2";
        var round2Body = "## Findings\n- still missing the GET endpoint";
        var round1OutputRef = new Uri("file:///tmp/r1-feedback.bin");
        var round2OutputRef = new Uri("file:///tmp/r2-feedback.bin");
        var round3OutputRef = new Uri("file:///tmp/r3-approved.bin");

        var artifactStore = new StubArtifactStore(uri =>
            uri == round1OutputRef ? round1Body
            : uri == round2OutputRef ? round2Body
            : "{}");

        var harness = BuildHarness(workflow, new Dictionary<string, int>(), artifactStore: artifactStore);
        await harness.Start();
        try
        {
            await PublishStart(harness, workflow, traceId, parentRoundId);
            var sagaHarness = harness.GetSagaStateMachineHarness<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
            await sagaHarness.Exists(traceId, x => x.Running);

            await harness.Bus.Publish(BuildCompletion(workflow, traceId, parentRoundId, "kickoff", 1, "Completed"));
            var round1 = (await WaitForPublishedAsync<SubflowInvokeRequested>(harness, expectedCount: 1))[0].Context.Message;

            // Round 1 reject — accumulator should record the artifact body before round 2 spawns.
            await harness.Bus.Publish(new SubflowCompleted(
                ParentTraceId: traceId, ParentNodeId: reviewLoopNodeId, ParentRoundId: parentRoundId,
                ChildTraceId: round1.ChildTraceId, OutputPortName: "Rejected",
                OutputRef: round1OutputRef,
                WorkflowContext: new Dictionary<string, JsonElement>(),
                Decision: "Rejected", ReviewRound: 1, TerminalPort: "Rejected"));

            var round2 = (await WaitForPublishedAsync<SubflowInvokeRequested>(harness, expectedCount: 2))[1].Context.Message;
            round2.ReviewRound.Should().Be(2);

            // Verify the accumulator captured round 1 before spawning round 2.
            var afterRound1Saga = sagaHarness.Sagas.Contains(traceId)!;
            var afterRound1Bag = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                afterRound1Saga.WorkflowInputsJson)!;
            afterRound1Bag.Should().ContainKey(RejectionHistoryAccumulator.WorkflowVariableKey);
            afterRound1Bag[RejectionHistoryAccumulator.WorkflowVariableKey].GetString()
                .Should().Contain("## Round 1").And.Contain("missing API in section 2");

            // Round 2 reject — second round body appends.
            await harness.Bus.Publish(new SubflowCompleted(
                ParentTraceId: traceId, ParentNodeId: reviewLoopNodeId, ParentRoundId: parentRoundId,
                ChildTraceId: round2.ChildTraceId, OutputPortName: "Rejected",
                OutputRef: round2OutputRef,
                WorkflowContext: new Dictionary<string, JsonElement>(),
                Decision: "Rejected", ReviewRound: 2, TerminalPort: "Rejected"));

            var round3 = (await WaitForPublishedAsync<SubflowInvokeRequested>(harness, expectedCount: 3))[2].Context.Message;
            round3.ReviewRound.Should().Be(3);

            var afterRound2Saga = sagaHarness.Sagas.Contains(traceId)!;
            var afterRound2Bag = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                afterRound2Saga.WorkflowInputsJson)!;
            var historyAfterRound2 = afterRound2Bag[RejectionHistoryAccumulator.WorkflowVariableKey].GetString()!;
            historyAfterRound2.Should().Contain("## Round 1").And.Contain("missing API in section 2");
            historyAfterRound2.Should().Contain("## Round 2").And.Contain("still missing the GET endpoint");

            // Round 3 approves — loop exits Approved, accumulator is NOT extended (approval is not
            // a rejection event), and the saga terminates cleanly.
            await harness.Bus.Publish(new SubflowCompleted(
                ParentTraceId: traceId, ParentNodeId: reviewLoopNodeId, ParentRoundId: parentRoundId,
                ChildTraceId: round3.ChildTraceId, OutputPortName: "Approved",
                OutputRef: round3OutputRef,
                WorkflowContext: new Dictionary<string, JsonElement>(),
                Decision: "Approved", ReviewRound: 3, TerminalPort: "Approved"));

            await sagaHarness.Exists(traceId, x => x.Completed);

            var finalSaga = sagaHarness.Sagas.Contains(traceId)!;
            var finalBag = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                finalSaga.WorkflowInputsJson)!;
            finalBag[RejectionHistoryAccumulator.WorkflowVariableKey].GetString()
                .Should().Be(historyAfterRound2,
                    "the Approved exit must not extend the rejection history");
        }
        finally { await harness.Stop(); }
    }

    [Fact]
    public async Task ReviewLoopCompleted_RejectionHistoryDisabled_DoesNotPopulateAccumulator()
    {
        // P3 CR1 contract: a ReviewLoop without the feature configured (NULL = pre-P3 row, or
        // explicit Enabled=false) must behave identically to today — no `__loop.rejectionHistory`
        // entry appears on the saga bag, so existing hand-rolled accumulation paths are untouched.
        var traceId = Guid.NewGuid();
        var parentRoundId = Guid.NewGuid();
        var startNodeId = Guid.NewGuid();
        var reviewLoopNodeId = Guid.NewGuid();

        var workflow = BuildWorkflowWithReviewLoop(
            "rl-rejection-history-disabled", startNodeId, "kickoff", reviewLoopNodeId,
            subflowKey: "critique-revise", subflowVersion: 1, maxRounds: 2,
            rejectionHistory: null);

        var artifactStore = new StubArtifactStore("## Findings\n- something to ignore");

        var harness = BuildHarness(workflow, new Dictionary<string, int>(), artifactStore: artifactStore);
        await harness.Start();
        try
        {
            await PublishStart(harness, workflow, traceId, parentRoundId);
            var sagaHarness = harness.GetSagaStateMachineHarness<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
            await sagaHarness.Exists(traceId, x => x.Running);

            await harness.Bus.Publish(BuildCompletion(workflow, traceId, parentRoundId, "kickoff", 1, "Completed"));
            var round1 = (await WaitForPublishedAsync<SubflowInvokeRequested>(harness, expectedCount: 1))[0].Context.Message;

            await harness.Bus.Publish(new SubflowCompleted(
                ParentTraceId: traceId, ParentNodeId: reviewLoopNodeId, ParentRoundId: parentRoundId,
                ChildTraceId: round1.ChildTraceId, OutputPortName: "Rejected",
                OutputRef: new Uri("file:///tmp/r1.bin"),
                WorkflowContext: new Dictionary<string, JsonElement>(),
                Decision: "Rejected", ReviewRound: 1, TerminalPort: "Rejected"));

            await WaitForPublishedAsync<SubflowInvokeRequested>(harness, expectedCount: 2);

            var saga = sagaHarness.Sagas.Contains(traceId)!;
            // Pre-P3 sagas leave WorkflowInputsJson null until something writes; an empty bag
            // is the expected disabled-feature outcome too.
            var bag = string.IsNullOrEmpty(saga.WorkflowInputsJson)
                ? new Dictionary<string, JsonElement>()
                : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(saga.WorkflowInputsJson)!;
            bag.Should().NotContainKey(RejectionHistoryAccumulator.WorkflowVariableKey);
        }
        finally { await harness.Stop(); }
    }

    [Fact]
    public async Task ReviewLoopCompleted_FailedOnRound2_ShouldExitFailedPort_AndKeepRound1GlobalMerged()
    {
        // Slice 10 scenario 5: a Failed return from round 2 exits the Failed port (no edge →
        // Failed terminal). Round 1's setWorkflow writes must still be visible on the parent's
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

            await harness.Bus.Publish(BuildCompletion(workflow, traceId, parentRoundId, "kickoff", 1, "Completed"));
            var round1 = (await WaitForPublishedAsync<SubflowInvokeRequested>(harness, expectedCount: 1))[0].Context.Message;

            // Round 1: Rejected (the loopDecision) with a setWorkflow write; next round spawns.
            await harness.Bus.Publish(new SubflowCompleted(
                ParentTraceId: traceId, ParentNodeId: reviewLoopNodeId, ParentRoundId: parentRoundId,
                ChildTraceId: round1.ChildTraceId, OutputPortName: "Rejected",
                OutputRef: new Uri("file:///tmp/r1.bin"),
                WorkflowContext: new Dictionary<string, JsonElement>
                {
                    ["fromRound1"] = JsonDocument.Parse("\"carried\"").RootElement.Clone()
                },
                Decision: "Rejected", ReviewRound: 1, TerminalPort: "Rejected"));

            var round2 = (await WaitForPublishedAsync<SubflowInvokeRequested>(harness, expectedCount: 2))[1].Context.Message;

            // Round 2: Failed → Failed port (no outgoing edge → Failed terminal).
            await harness.Bus.Publish(new SubflowCompleted(
                ParentTraceId: traceId, ParentNodeId: reviewLoopNodeId, ParentRoundId: parentRoundId,
                ChildTraceId: round2.ChildTraceId, OutputPortName: "Failed",
                OutputRef: new Uri("file:///tmp/r2-failed.bin"),
                WorkflowContext: new Dictionary<string, JsonElement>(),
                Decision: "Failed", ReviewRound: 2, TerminalPort: "Failed"));

            await sagaHarness.Exists(traceId, x => x.Failed);

            var resumed = sagaHarness.Sagas.Contains(traceId)!;
            resumed.WorkflowInputsJson.Should().NotBeNullOrWhiteSpace();
            resumed.WorkflowInputsJson!.Should().Contain("fromRound1",
                "round 1's setWorkflow write must survive even when a later round fails");
        }
        finally { await harness.Stop(); }
    }

    // ReviewLoopCompleted_EscalatedFromChild_ShouldExitFailedPort was deleted in Slice 5: the
    // Escalation node was removed in Slice 2, and the new port model propagates terminal port
    // names verbatim — there is no Escalated → Failed collapse.

    // ReviewLoopCompleted_RejectedDecisionOnFailedSaga_ShouldAdvanceToNextRound was deleted in
    // Slice 5: ResolveReviewLoopOutcome no longer consults the Decision field. It now uses
    // TerminalPort (with OutputPortName as a fallback). The Decision-vs-OutputPortName
    // precedence rule that this regression test asserted no longer exists.

    [Fact]
    public async Task ReviewLoopCompleted_CustomLoopDecision_ShouldIterateOnMatchingTerminalPort()
    {
        // Configurable loop decision: when LoopDecision = "Answered", a child whose terminal
        // effective port is "Answered" triggers another iteration. Default "Rejected" is
        // overridden on the ReviewLoop node; Rejected alone no longer triggers iteration.
        var traceId = Guid.NewGuid();
        var parentRoundId = Guid.NewGuid();
        var startNodeId = Guid.NewGuid();
        var reviewLoopNodeId = Guid.NewGuid();

        var workflow = BuildWorkflowWithReviewLoop(
            "rl-custom-loop-decision", startNodeId, "kickoff", reviewLoopNodeId,
            subflowKey: "socratic-interview", subflowVersion: 1, maxRounds: 3,
            loopDecision: "Answered");

        var harness = BuildHarness(workflow, new Dictionary<string, int>());
        await harness.Start();
        try
        {
            await PublishStart(harness, workflow, traceId, parentRoundId);
            var sagaHarness = harness.GetSagaStateMachineHarness<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
            await sagaHarness.Exists(traceId, x => x.Running);

            await harness.Bus.Publish(BuildCompletion(workflow, traceId, parentRoundId, "kickoff", 1, "Completed"));
            var round1 = (await WaitForPublishedAsync<SubflowInvokeRequested>(harness, expectedCount: 1))[0].Context.Message;

            // Spawned SubflowInvokeRequested must carry the configured LoopDecision so the
            // child saga can recognize "Answered" as a legal clean-exit port.
            round1.LoopDecision.Should().Be("Answered");

            // Child completion with TerminalPort = "Answered" — the script picked a custom port.
            // Decision kind is Completed (the agent's underlying decision); the LoopDecision
            // match on TerminalPort should drive iteration regardless of Decision.
            await harness.Bus.Publish(new SubflowCompleted(
                ParentTraceId: traceId, ParentNodeId: reviewLoopNodeId, ParentRoundId: parentRoundId,
                ChildTraceId: round1.ChildTraceId, OutputPortName: "Completed",
                OutputRef: new Uri("file:///tmp/r1.bin"),
                WorkflowContext: new Dictionary<string, JsonElement>(),
                Decision: "Completed",
                ReviewRound: 1,
                TerminalPort: "Answered"));

            var round2 = (await WaitForPublishedAsync<SubflowInvokeRequested>(harness, expectedCount: 2))[1].Context.Message;
            round2.ReviewRound.Should().Be(2, "TerminalPort = LoopDecision triggers iteration");
            round2.LoopDecision.Should().Be("Answered");

            // Round 2: child emits Decision=Approved (someone hit approve instead of another
            // answer) — under custom LoopDecision, that's a Decision-Approved path → Approved.
            await harness.Bus.Publish(new SubflowCompleted(
                ParentTraceId: traceId, ParentNodeId: reviewLoopNodeId, ParentRoundId: parentRoundId,
                ChildTraceId: round2.ChildTraceId, OutputPortName: "Completed",
                OutputRef: new Uri("file:///tmp/r2.bin"),
                WorkflowContext: new Dictionary<string, JsonElement>(),
                Decision: "Approved",
                ReviewRound: 2,
                TerminalPort: "Approved"));

            await sagaHarness.Exists(traceId, x => x.Completed);
            var resumed = sagaHarness.Sagas.Contains(traceId)!;
            resumed.GetDecisionHistory().Should()
                .Contain(d => d.NodeId == reviewLoopNodeId && d.OutputPortName == "Approved",
                    "Approved Decision on a custom-LoopDecision loop still exits Approved");
        }
        finally { await harness.Stop(); }
    }

    [Fact]
    public async Task ReviewLoopCompleted_DefaultLoopDecision_RejectedTerminalPort_ShouldIterate()
    {
        // Regression: the default LoopDecision = "Rejected" keeps backward-compat behaviour.
        // A child whose TerminalPort = "Rejected" iterates; Decision-kind Rejected also works
        // (since the default effective-port-derivation maps Decision=Rejected to port "Rejected").
        var traceId = Guid.NewGuid();
        var parentRoundId = Guid.NewGuid();
        var startNodeId = Guid.NewGuid();
        var reviewLoopNodeId = Guid.NewGuid();

        var workflow = BuildWorkflowWithReviewLoop(
            "rl-default-loop-decision", startNodeId, "kickoff", reviewLoopNodeId,
            subflowKey: "critique-revise", subflowVersion: 1, maxRounds: 3,
            loopDecision: null); // default = "Rejected"

        var harness = BuildHarness(workflow, new Dictionary<string, int>());
        await harness.Start();
        try
        {
            await PublishStart(harness, workflow, traceId, parentRoundId);
            var sagaHarness = harness.GetSagaStateMachineHarness<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
            await sagaHarness.Exists(traceId, x => x.Running);

            await harness.Bus.Publish(BuildCompletion(workflow, traceId, parentRoundId, "kickoff", 1, "Completed"));
            var round1 = (await WaitForPublishedAsync<SubflowInvokeRequested>(harness, expectedCount: 1))[0].Context.Message;

            round1.LoopDecision.Should().Be("Rejected", "default LoopDecision is propagated as Rejected");

            // Simulate the standard case: reviewer Decision = Rejected with TerminalPort = "Rejected".
            await harness.Bus.Publish(new SubflowCompleted(
                ParentTraceId: traceId, ParentNodeId: reviewLoopNodeId, ParentRoundId: parentRoundId,
                ChildTraceId: round1.ChildTraceId, OutputPortName: "Completed",
                OutputRef: new Uri("file:///tmp/r1.bin"),
                WorkflowContext: new Dictionary<string, JsonElement>(),
                Decision: "Rejected",
                ReviewRound: 1,
                TerminalPort: "Rejected"));

            var round2 = (await WaitForPublishedAsync<SubflowInvokeRequested>(harness, expectedCount: 2))[1].Context.Message;
            round2.ReviewRound.Should().Be(2);
        }
        finally { await harness.Stop(); }
    }

    [Fact]
    public async Task ChildSaga_UnwiredPortMatchingParentLoopDecision_ShouldTerminateCompleted()
    {
        // The child saga's unwired-port allowlist includes not just Completed/Approved/Rejected
        // but also any port name matching the parent's configured LoopDecision. This lets a
        // socratic-style routing script pick a custom port name (e.g. "Answered") and have the
        // child saga exit cleanly — without that rule, the child would fail with "no outgoing
        // edge" every time the loop signal fired.
        var parentTraceId = Guid.NewGuid();
        var parentNodeId = Guid.NewGuid();
        var parentRoundId = Guid.NewGuid();
        var childTraceId = Guid.NewGuid();

        var childStartNodeId = Guid.NewGuid();
        var childWorkflow = new Workflow(
            Key: "custom-port-child",
            Version: 1,
            Name: "custom-port-child",
            MaxRoundsPerRound: 5,
            CreatedAtUtc: DateTime.UtcNow,
            Nodes: new[]
            {
                new WorkflowNode(childStartNodeId, WorkflowNodeKind.Start, "interviewer-agent",
                    AgentVersion: null,
                    OutputScript: "setNodePath('Answered');",
                    OutputPorts: new[] { "Completed", "Answered", "Failed" },
                    LayoutX: 0, LayoutY: 0),
            },
            Edges: Array.Empty<WorkflowEdge>(),
            Inputs: Array.Empty<WorkflowInput>());

        var harness = BuildHarness(childWorkflow, new Dictionary<string, int> { ["interviewer-agent"] = 1 });
        await harness.Start();
        try
        {
            await harness.Bus.Publish(new SubflowInvokeRequested(
                ParentTraceId: parentTraceId,
                ParentNodeId: parentNodeId,
                ParentRoundId: parentRoundId,
                ChildTraceId: childTraceId,
                SubflowKey: "custom-port-child",
                SubflowVersion: 1,
                InputRef: new Uri("file:///tmp/in.bin"),
                WorkflowContext: new Dictionary<string, JsonElement>(),
                Depth: 1,
                LoopDecision: "Answered"));

            var sagaHarness = harness.GetSagaStateMachineHarness<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
            await sagaHarness.Exists(childTraceId, x => x.Running);

            var invocations = await WaitForPublishedAsync<AgentInvokeRequested>(harness, expectedCount: 1);
            var invocation = invocations[0].Context.Message;

            // Agent returns Completed; routing script will pick "Answered" which is unwired.
            await harness.Bus.Publish(new AgentInvocationCompleted(
                TraceId: childTraceId,
                RoundId: invocation.RoundId,
                FromNodeId: childStartNodeId,
                AgentKey: "interviewer-agent",
                AgentVersion: 1,
                OutputPortName: "Completed",
                OutputRef: new Uri("file:///tmp/out.bin"),
                DecisionPayload: JsonDocument.Parse("{\"kind\":\"Completed\"}").RootElement,
                Duration: TimeSpan.FromMilliseconds(1),
                TokenUsage: new Contracts.TokenUsage(0, 0, 0)));

            await sagaHarness.Exists(childTraceId, x => x.Completed);

            var terminal = sagaHarness.Sagas.Contains(childTraceId)!;
            terminal.LastEffectivePort.Should().Be("Answered");
            terminal.ParentLoopDecision.Should().Be("Answered");
            terminal.FailureReason.Should().BeNull();

            var completions = await WaitForPublishedAsync<SubflowCompleted>(harness, expectedCount: 1);
            completions[0].Context.Message.TerminalPort.Should().Be("Answered",
                "TerminalPort rides up to the parent so ReviewLoop can compare against LoopDecision");
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

            await harness.Bus.Publish(BuildCompletion(workflow, traceId, roundId, "kickoff", 1, "Completed"));

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

    [Theory]
    [InlineData("Rejected")]
    [InlineData("Approved")]
    public async Task ChildSaga_WithUnwiredApprovedOrRejectedPort_ShouldTerminateCompletedWithDecisionPreserved(
        string childDecision)
    {
        // Subflow exit rule: an unwired Approved or Rejected port on a CHILD saga is a legal
        // clean exit — the saga terminates in Completed state and the agent's decision kind is
        // preserved on SubflowCompleted.Decision so the parent (especially a ReviewLoop) can
        // route on the last agent's intent. Without this rule, the child would have to wire
        // every non-Completed port somewhere just to avoid Failed, and ReviewLoops would show
        // up in the UI as failed child traces every time the reviewer rejected.
        var parentTraceId = Guid.NewGuid();
        var parentNodeId = Guid.NewGuid();
        var parentRoundId = Guid.NewGuid();
        var childTraceId = Guid.NewGuid();

        var childStartNodeId = Guid.NewGuid();
        var childWorkflow = new Workflow(
            Key: "reviewer-only",
            Version: 1,
            Name: "reviewer-only",
            MaxRoundsPerRound: 5,
            CreatedAtUtc: DateTime.UtcNow,
            Nodes: new[]
            {
                new WorkflowNode(childStartNodeId, WorkflowNodeKind.Start, "reviewer-agent",
                    AgentVersion: null, OutputScript: null,
                    OutputPorts: new[] { "Completed", "Approved", "Rejected", "Failed" },
                    LayoutX: 0, LayoutY: 0),
            },
            Edges: Array.Empty<WorkflowEdge>(), // no wired ports — all exits are unwired
            Inputs: Array.Empty<WorkflowInput>());

        var harness = BuildHarness(childWorkflow, new Dictionary<string, int> { ["reviewer-agent"] = 1 });
        await harness.Start();
        try
        {
            await harness.Bus.Publish(new SubflowInvokeRequested(
                ParentTraceId: parentTraceId,
                ParentNodeId: parentNodeId,
                ParentRoundId: parentRoundId,
                ChildTraceId: childTraceId,
                SubflowKey: "reviewer-only",
                SubflowVersion: 1,
                InputRef: new Uri("file:///tmp/reviewer-in.bin"),
                WorkflowContext: new Dictionary<string, JsonElement>(),
                Depth: 1));

            var sagaHarness = harness.GetSagaStateMachineHarness<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
            await sagaHarness.Exists(childTraceId, x => x.Running);

            // Reviewer agent emits Rejected (or Approved) via the submit tool.
            var invocations = await WaitForPublishedAsync<AgentInvokeRequested>(harness, expectedCount: 1);
            var invocation = invocations[0].Context.Message;

            await harness.Bus.Publish(new AgentInvocationCompleted(
                TraceId: childTraceId,
                RoundId: invocation.RoundId,
                FromNodeId: childStartNodeId,
                AgentKey: "reviewer-agent",
                AgentVersion: 1,
                OutputPortName: childDecision,
                OutputRef: new Uri("file:///tmp/reviewer-out.bin"),
                DecisionPayload: JsonDocument.Parse($"{{\"portName\":\"{childDecision}\"}}").RootElement.Clone(),
                Duration: TimeSpan.FromMilliseconds(1),
                TokenUsage: new Contracts.TokenUsage(0, 0, 0)));

            // Child saga must terminate as Completed (not Failed), even though the emitted port
            // had no outgoing edge. The old rule ("unwired non-Completed = Failed") would have
            // produced a Failed terminal with a "No outgoing edge" FailureReason.
            await sagaHarness.Exists(childTraceId, x => x.Completed);

            var terminal = sagaHarness.Sagas.Contains(childTraceId)!;
            terminal.CurrentState.Should().Be(nameof(WorkflowSagaStateMachine.Completed));
            terminal.FailureReason.Should().BeNull(
                "an unwired Approved/Rejected port is a legal exit, not an error");

            // SubflowCompleted must carry the decision kind the reviewer emitted so a ReviewLoop
            // parent can iterate on Rejected without re-interpreting a Failed saga state.
            var completions = await WaitForPublishedAsync<SubflowCompleted>(harness, expectedCount: 1);
            completions.Should().HaveCount(1);
            var completion = completions[0].Context.Message;
            completion.Decision.Should().Be(childDecision);
            completion.OutputPortName.Should().Be("Completed",
                "unwired Approved/Rejected ports cleanly complete the child saga");
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
                    AgentVersion: null, OutputScript: null, OutputPorts: AllDecisionPorts, LayoutX: 0, LayoutY: 0),
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
                WorkflowContext: sharedContext,
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
                "inherited parent state belongs on WorkflowContext, not ContextInputs — the child's local context starts empty");
            dispatch.WorkflowContext.Should().NotBeNull();
            dispatch.WorkflowContext!.Should().ContainKey("sharedFlag",
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
            saga.WorkflowInputsJson.Should().NotBeNullOrWhiteSpace();
            saga.WorkflowInputsJson!.Should().Contain("sharedFlag");
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
            new(startNodeId, WorkflowNodeKind.Start, startAgentKey, AgentVersion: null, OutputScript: null,
                OutputPorts: AllDecisionPorts, LayoutX: 0, LayoutY: 0),
            new(subflowNodeId, WorkflowNodeKind.Subflow, AgentKey: null, AgentVersion: null, OutputScript: null,
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
        int maxRounds,
        string? loopDecision = null,
        RejectionHistoryConfig? rejectionHistory = null)
    {
        var nodes = new List<WorkflowNode>
        {
            new(startNodeId, WorkflowNodeKind.Start, startAgentKey, AgentVersion: null, OutputScript: null,
                OutputPorts: AllDecisionPorts, LayoutX: 0, LayoutY: 0),
            new(reviewLoopNodeId, WorkflowNodeKind.ReviewLoop, AgentKey: null, AgentVersion: null, OutputScript: null,
                OutputPorts: new[] { "Approved", "Exhausted", "Failed" }, LayoutX: 250, LayoutY: 0,
                SubflowKey: subflowKey, SubflowVersion: subflowVersion, ReviewMaxRounds: maxRounds,
                LoopDecision: loopDecision,
                RejectionHistory: rejectionHistory),
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
        IArtifactStore? artifactStore = null,
        IReadOnlyDictionary<string, AgentConfig>? agentConfigs = null)
    {
        var provider = new ServiceCollection()
            .AddSingleton<IWorkflowRepository>(new FakeWorkflowRepository(workflow))
            .AddSingleton<IAgentConfigRepository>(new FakeAgentConfigRepository(agentVersions, agentConfigs))
            .AddSingleton<IArtifactStore>(artifactStore ?? new StubArtifactStore(defaultJson: "{}"))
            .AddSingleton<IMemoryCache>(_ => new MemoryCache(new MemoryCacheOptions()))
            .AddSingleton<LogicNodeScriptHost>()
            .AddSingleton<Runtime.IScribanTemplateRenderer, Runtime.ScribanTemplateRenderer>()
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

    /// <summary>
    /// Artifact store that records every write and preserves seeded reads. Used by tests that
    /// verify setOutput()-generated artifacts land in the store and that the original agent
    /// submission is never clobbered.
    /// </summary>
    private sealed class RecordingArtifactStore : IArtifactStore
    {
        private readonly Dictionary<Uri, byte[]> writes = new();
        public Dictionary<Uri, string> SeededReads { get; } = new();

        public void SeedRead(Uri uri, string content) => SeededReads[uri] = content;

        public string ReadWrittenContent(Uri uri) =>
            writes.TryGetValue(uri, out var bytes)
                ? Encoding.UTF8.GetString(bytes)
                : throw new InvalidOperationException($"No write recorded for {uri}");

        public Task<ArtifactMetadata> GetMetadataAsync(Uri uri, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Stream> ReadAsync(Uri uri, CancellationToken cancellationToken = default)
        {
            if (writes.TryGetValue(uri, out var recorded))
            {
                return Task.FromResult<Stream>(new MemoryStream(recorded));
            }
            if (SeededReads.TryGetValue(uri, out var seeded))
            {
                return Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(seeded)));
            }
            return Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes("{}")));
        }

        public Task<Uri> WriteAsync(Stream content, ArtifactMetadata metadata, CancellationToken cancellationToken = default)
        {
            using var buffer = new MemoryStream();
            content.CopyTo(buffer);
            var uri = new Uri($"file:///recorded/{metadata.ArtifactId:N}");
            writes[uri] = buffer.ToArray();
            return Task.FromResult(uri);
        }
    }

    private sealed record EdgeSpec(
        string From,
        string Decision,
        string To,
        bool RotatesRound,
        int SortOrder);

    private static EdgeSpec Edge(
        string from,
        string decision,
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
        IReadOnlyList<EdgeSpec> edges)
    {
        var agentKeys = new HashSet<string>(StringComparer.Ordinal) { startAgentKey };
        foreach (var edge in edges)
        {
            agentKeys.Add(edge.From);
            agentKeys.Add(edge.To);
        }

        var nodes = new List<WorkflowNode>();
        var nodeIds = new Dictionary<string, Guid>(StringComparer.Ordinal);

        foreach (var agentKey in agentKeys)
        {
            var kind = string.Equals(agentKey, startAgentKey, StringComparison.Ordinal)
                ? WorkflowNodeKind.Start
                : WorkflowNodeKind.Agent;
            var id = Guid.NewGuid();
            nodeIds[agentKey] = id;

            nodes.Add(new WorkflowNode(id, kind, agentKey, AgentVersion: null, OutputScript: null,
                OutputPorts: AllDecisionPorts, LayoutX: 0, LayoutY: 0));
        }

        var workflowEdges = edges
            .Select((edge, index) => new WorkflowEdge(
                FromNodeId: nodeIds[edge.From],
                FromPort: edge.Decision,
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
        new[] { "Completed", "Approved", "Rejected", "Failed" };

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
        string decision)
    {
        return new AgentInvocationCompleted(
            TraceId: traceId,
            RoundId: roundId,
            FromNodeId: NodeIdFor(workflow, agentKey),
            AgentKey: agentKey,
            AgentVersion: agentVersion,
            OutputPortName: decision,
            OutputRef: new Uri($"file:///tmp/{agentKey}-out.bin"),
            DecisionPayload: JsonDocument.Parse($"{{\"portName\":\"{decision}\"}}").RootElement.Clone(),
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

        public Task<IReadOnlyCollection<string>> GetTerminalPortsAsync(
            string key,
            int version,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(workflow.TerminalPorts);

        public Task<int> CreateNewVersionAsync(WorkflowDraft draft, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeAgentConfigRepository(
        IReadOnlyDictionary<string, int> versions,
        IReadOnlyDictionary<string, AgentConfig>? configs = null)
        : IAgentConfigRepository
    {
        public Task<AgentConfig> GetAsync(string key, int version, CancellationToken cancellationToken = default)
        {
            if (configs is not null && configs.TryGetValue(key, out var explicitConfig))
            {
                return Task.FromResult(explicitConfig);
            }

            // Default agent config with no decision-output templates — preserves pre-feature behavior
            // for tests that don't care about templating.
            var empty = new AgentConfig(
                Key: key,
                Version: version,
                Kind: AgentKind.Agent,
                Configuration: new Runtime.AgentInvocationConfiguration(
                    Provider: "openai",
                    Model: "gpt-test"),
                ConfigJson: "{}",
                CreatedAtUtc: DateTime.UtcNow,
                CreatedBy: null);
            return Task.FromResult(empty);
        }

        public Task<int> CreateNewVersionAsync(string key, string configJson, string? createdBy, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> GetLatestVersionAsync(string key, CancellationToken cancellationToken = default) =>
            versions.TryGetValue(key, out var version)
                ? Task.FromResult(version)
                : throw new AgentConfigNotFoundException(key, 0);

        public Task<bool> RetireAsync(string key, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AgentConfig> CreateForkAsync(
            string sourceKey,
            int sourceVersion,
            string workflowKey,
            string configJson,
            string? createdBy,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> CreatePublishedVersionAsync(
            string targetKey,
            string configJson,
            string forkedFromKey,
            int forkedFromVersion,
            string? createdBy,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
