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

namespace CodeFlow.Orchestration.Tests;

/// <summary>
/// sc-627: end-to-end coverage of the boundary input/output script slots on Subflow and
/// ReviewLoop nodes. Drives child saga completions by hand the same way
/// WorkflowSagaSubflowEndToEndTests does, with an artifact store that records writes so
/// setOutput / setInput overrides are observable.
/// </summary>
[Collection("Bus integration")]
public sealed class WorkflowSagaBoundaryScriptTests
{
    private static readonly IReadOnlyList<string> AllPorts =
        new[] { "Completed", "Approved", "Rejected", "Failed" };

    [Fact]
    public async Task SubflowInputScript_SetInput_RewritesArtifactPassedToChildSaga()
    {
        // Parent: Start(kickoff) → Subflow(child) where the Subflow node has an input script
        // that rewrites the artifact via setInput. Assert: the SubflowInvokeRequested carries
        // the override URI (not the upstream URI) and the child's first AgentInvokeRequested
        // sees the override too.
        var parentTraceId = Guid.NewGuid();
        var parentRoundId = Guid.NewGuid();
        var parentStartId = Guid.NewGuid();
        var parentSubflowId = Guid.NewGuid();
        var childStartId = Guid.NewGuid();

        const string inputScript = "setInput('rewritten by boundary input');";

        var parent = new Workflow(
            Key: "boundary-in-parent",
            Version: 1,
            Name: "boundary-in-parent",
            MaxRoundsPerRound: 5,
            CreatedAtUtc: DateTime.UtcNow,
            Nodes: new[]
            {
                new WorkflowNode(parentStartId, WorkflowNodeKind.Start, "kickoff", AgentVersion: 1,
                    OutputScript: null, OutputPorts: AllPorts, LayoutX: 0, LayoutY: 0),
                new WorkflowNode(parentSubflowId, WorkflowNodeKind.Subflow, AgentKey: null, AgentVersion: null,
                    OutputScript: null, OutputPorts: new[] { "Completed", "Failed" },
                    LayoutX: 250, LayoutY: 0,
                    SubflowKey: "boundary-in-child", SubflowVersion: 1,
                    InputScript: inputScript),
            },
            Edges: new[]
            {
                new WorkflowEdge(parentStartId, "Completed", parentSubflowId,
                    WorkflowEdge.DefaultInputPort, false, 0),
            },
            Inputs: Array.Empty<WorkflowInput>());

        var child = new Workflow(
            Key: "boundary-in-child",
            Version: 1,
            Name: "boundary-in-child",
            MaxRoundsPerRound: 5,
            CreatedAtUtc: DateTime.UtcNow,
            Nodes: new[]
            {
                new WorkflowNode(childStartId, WorkflowNodeKind.Start, "child-agent", AgentVersion: 1,
                    OutputScript: null, OutputPorts: AllPorts, LayoutX: 0, LayoutY: 0),
            },
            Edges: Array.Empty<WorkflowEdge>(),
            Inputs: Array.Empty<WorkflowInput>());

        var artifactStore = new RecordingArtifactStore();
        var upstreamRef = new Uri("file:///tmp/parent-start-out.bin");
        artifactStore.SeedRead(upstreamRef, "{}");

        await using var scope = BuildHarness(new[] { parent, child },
            new Dictionary<string, int> { ["kickoff"] = 1, ["child-agent"] = 1 },
            artifactStore);
        var harness = scope.Harness;
        await harness.Start();
        try
        {
            await harness.Bus.Publish(new AgentInvokeRequested(
                TraceId: parentTraceId,
                RoundId: parentRoundId,
                WorkflowKey: parent.Key,
                WorkflowVersion: parent.Version,
                NodeId: parentStartId,
                AgentKey: "kickoff",
                AgentVersion: 1,
                InputRef: new Uri("file:///tmp/parent-in.bin"),
                ContextInputs: new Dictionary<string, JsonElement>()));

            var sagaHarness = harness.GetSagaStateMachineHarness<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
            await sagaHarness.Exists(parentTraceId, x => x.Running);

            // Parent Start completes → saga routes to Subflow node → input script runs →
            // SubflowInvokeRequested carries the override URI.
            await harness.Bus.Publish(BuildCompletion(parentTraceId, parentRoundId, parentStartId, "kickoff", 1,
                "Completed", upstreamRef.ToString()));

            var spawns = await WaitForPublishedAsync<SubflowInvokeRequested>(harness, 1);
            var spawn = spawns[0].Context.Message;

            spawn.InputRef.Should().NotBe(upstreamRef,
                "boundary input script's setInput() must replace the URI passed to the child saga");
            artifactStore.ReadWrittenContent(spawn.InputRef)
                .Should().Be("rewritten by boundary input");

            // Child saga is initialized with the override URI; its first AgentInvokeRequested
            // also points at the override.
            var childTraceId = spawn.ChildTraceId;
            await sagaHarness.Exists(childTraceId, x => x.Running);

            var childInvokes = await WaitForPublishedAsync<AgentInvokeRequested>(harness, 2);
            var childInvoke = childInvokes
                .Select(m => m.Context.Message)
                .Single(m => m.NodeId == childStartId && m.TraceId == childTraceId);
            childInvoke.InputRef.Should().Be(spawn.InputRef);
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task SubflowOutputScript_SetOutputAndSetNodePath_RewriteArtifactAndPort()
    {
        // Parent: Start → Subflow → Agent('downstream') wired off "Reroute" port (not the
        // child's terminal port). The boundary output script calls setOutput() and
        // setNodePath('Reroute'). Assert: downstream agent's InputRef = override URI;
        // synthetic DecisionRecord on the Subflow node carries the rewritten port and URI.
        var parentTraceId = Guid.NewGuid();
        var parentRoundId = Guid.NewGuid();
        var parentStartId = Guid.NewGuid();
        var parentSubflowId = Guid.NewGuid();
        var parentDownstreamId = Guid.NewGuid();
        var childStartId = Guid.NewGuid();

        const string outputScript = """
            setOutput('rewritten by boundary output');
            setNodePath('Reroute');
            """;

        var parent = new Workflow(
            Key: "boundary-out-parent",
            Version: 1,
            Name: "boundary-out-parent",
            MaxRoundsPerRound: 5,
            CreatedAtUtc: DateTime.UtcNow,
            Nodes: new[]
            {
                new WorkflowNode(parentStartId, WorkflowNodeKind.Start, "kickoff", AgentVersion: 1,
                    OutputScript: null, OutputPorts: AllPorts, LayoutX: 0, LayoutY: 0),
                new WorkflowNode(parentSubflowId, WorkflowNodeKind.Subflow, AgentKey: null, AgentVersion: null,
                    OutputScript: outputScript, OutputPorts: new[] { "Completed", "Reroute", "Failed" },
                    LayoutX: 250, LayoutY: 0,
                    SubflowKey: "boundary-out-child", SubflowVersion: 1),
                new WorkflowNode(parentDownstreamId, WorkflowNodeKind.Agent, "downstream", AgentVersion: 1,
                    OutputScript: null, OutputPorts: AllPorts, LayoutX: 500, LayoutY: 0),
            },
            Edges: new[]
            {
                new WorkflowEdge(parentStartId, "Completed", parentSubflowId,
                    WorkflowEdge.DefaultInputPort, false, 0),
                new WorkflowEdge(parentSubflowId, "Reroute", parentDownstreamId,
                    WorkflowEdge.DefaultInputPort, false, 1),
            },
            Inputs: Array.Empty<WorkflowInput>());

        var child = new Workflow(
            Key: "boundary-out-child",
            Version: 1,
            Name: "boundary-out-child",
            MaxRoundsPerRound: 5,
            CreatedAtUtc: DateTime.UtcNow,
            Nodes: new[]
            {
                new WorkflowNode(childStartId, WorkflowNodeKind.Start, "child-agent", AgentVersion: 1,
                    OutputScript: null, OutputPorts: AllPorts, LayoutX: 0, LayoutY: 0),
            },
            Edges: Array.Empty<WorkflowEdge>(),
            Inputs: Array.Empty<WorkflowInput>());

        var artifactStore = new RecordingArtifactStore();
        var childOutputRef = new Uri("file:///tmp/child-out.bin");
        artifactStore.SeedRead(childOutputRef, """{"answer":"original"}""");

        await using var scope = BuildHarness(new[] { parent, child },
            new Dictionary<string, int> { ["kickoff"] = 1, ["child-agent"] = 1, ["downstream"] = 1 },
            artifactStore);
        var harness = scope.Harness;
        await harness.Start();
        try
        {
            await harness.Bus.Publish(new AgentInvokeRequested(
                TraceId: parentTraceId,
                RoundId: parentRoundId,
                WorkflowKey: parent.Key,
                WorkflowVersion: parent.Version,
                NodeId: parentStartId,
                AgentKey: "kickoff",
                AgentVersion: 1,
                InputRef: new Uri("file:///tmp/parent-in.bin"),
                ContextInputs: new Dictionary<string, JsonElement>()));

            var sagaHarness = harness.GetSagaStateMachineHarness<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
            await sagaHarness.Exists(parentTraceId, x => x.Running);

            await harness.Bus.Publish(BuildCompletion(parentTraceId, parentRoundId, parentStartId, "kickoff", 1,
                "Completed", "file:///tmp/parent-start-out.bin"));

            var spawns = await WaitForPublishedAsync<SubflowInvokeRequested>(harness, 1);
            var childTraceId = spawns[0].Context.Message.ChildTraceId;
            await sagaHarness.Exists(childTraceId, x => x.Running);

            var childInvokes = await WaitForPublishedAsync<AgentInvokeRequested>(harness, 2);
            var childInvoke = childInvokes
                .Select(m => m.Context.Message)
                .Single(m => m.NodeId == childStartId && m.TraceId == childTraceId);

            // Child completes on "Completed" port — boundary output script then rewrites both
            // the URI and the routing port to "Reroute".
            await harness.Bus.Publish(BuildCompletion(childTraceId, childInvoke.RoundId, childStartId,
                "child-agent", 1, "Completed", childOutputRef.ToString()));

            await sagaHarness.Exists(childTraceId, x => x.Completed);

            // Parent's downstream agent should be invoked on the rerouted port with the
            // overridden artifact.
            await SpinUntilAsync(() => harness.Published.Select<AgentInvokeRequested>()
                .Any(m => m.Context.Message.AgentKey == "downstream" && m.Context.Message.TraceId == parentTraceId));

            var downstream = harness.Published.Select<AgentInvokeRequested>()
                .Single(m => m.Context.Message.AgentKey == "downstream"
                    && m.Context.Message.TraceId == parentTraceId)
                .Context.Message;

            downstream.InputRef.Should().NotBe(childOutputRef,
                "boundary output script's setOutput() must rewrite the artifact handed downstream");
            artifactStore.ReadWrittenContent(downstream.InputRef!)
                .Should().Be("rewritten by boundary output");

            var parentSaga = sagaHarness.Sagas.Contains(parentTraceId)!;
            var subflowDecision = parentSaga.GetDecisionHistory()
                .Single(d => d.NodeId == parentSubflowId);
            subflowDecision.OutputPortName.Should().Be("Reroute",
                "synthetic decision must reflect the script-chosen port");
            subflowDecision.OutputRef.Should().Be(downstream.InputRef!.ToString(),
                "synthetic decision's OutputRef must be the override URI, not the child's terminal URI");
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task ReviewLoopInputScript_RunsExactlyOnce_NotPerIteration()
    {
        // ReviewLoop wired with maxRounds=3 and an input script that increments a counter via
        // setWorkflow. Drive 2 rejected rounds + 1 approved round. Assert: counter = 1
        // (not 3) — input script fires once before round 1, never on round 2 or 3.
        var parentTraceId = Guid.NewGuid();
        var parentRoundId = Guid.NewGuid();
        var parentStartId = Guid.NewGuid();
        var parentLoopId = Guid.NewGuid();
        var childStartId = Guid.NewGuid();

        const string inputScript = """
            var n = workflow.boundaryInputCount || 0;
            setWorkflow('boundaryInputCount', n + 1);
            """;

        var parent = new Workflow(
            Key: "boundary-rl-input-parent",
            Version: 1,
            Name: "boundary-rl-input-parent",
            MaxRoundsPerRound: 5,
            CreatedAtUtc: DateTime.UtcNow,
            Nodes: new[]
            {
                new WorkflowNode(parentStartId, WorkflowNodeKind.Start, "kickoff", AgentVersion: 1,
                    OutputScript: null, OutputPorts: AllPorts, LayoutX: 0, LayoutY: 0),
                new WorkflowNode(parentLoopId, WorkflowNodeKind.ReviewLoop, AgentKey: null, AgentVersion: null,
                    OutputScript: null, OutputPorts: new[] { "Approved", "Failed" },
                    LayoutX: 250, LayoutY: 0,
                    SubflowKey: "boundary-rl-input-child", SubflowVersion: 1,
                    InputScript: inputScript,
                    ReviewMaxRounds: 3,
                    LoopDecision: "Rejected"),
            },
            Edges: new[]
            {
                new WorkflowEdge(parentStartId, "Completed", parentLoopId,
                    WorkflowEdge.DefaultInputPort, false, 0),
            },
            Inputs: Array.Empty<WorkflowInput>());

        var child = new Workflow(
            Key: "boundary-rl-input-child",
            Version: 1,
            Name: "boundary-rl-input-child",
            MaxRoundsPerRound: 5,
            CreatedAtUtc: DateTime.UtcNow,
            Nodes: new[]
            {
                new WorkflowNode(childStartId, WorkflowNodeKind.Start, "reviewer", AgentVersion: 1,
                    OutputScript: null, OutputPorts: AllPorts, LayoutX: 0, LayoutY: 0),
            },
            Edges: Array.Empty<WorkflowEdge>(),
            Inputs: Array.Empty<WorkflowInput>());

        var artifactStore = new RecordingArtifactStore();

        await using var scope = BuildHarness(new[] { parent, child },
            new Dictionary<string, int> { ["kickoff"] = 1, ["reviewer"] = 1 },
            artifactStore);
        var harness = scope.Harness;
        await harness.Start();
        try
        {
            await harness.Bus.Publish(new AgentInvokeRequested(
                TraceId: parentTraceId,
                RoundId: parentRoundId,
                WorkflowKey: parent.Key,
                WorkflowVersion: parent.Version,
                NodeId: parentStartId,
                AgentKey: "kickoff",
                AgentVersion: 1,
                InputRef: new Uri("file:///tmp/parent-in.bin"),
                ContextInputs: new Dictionary<string, JsonElement>()));

            var sagaHarness = harness.GetSagaStateMachineHarness<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
            await sagaHarness.Exists(parentTraceId, x => x.Running);

            await harness.Bus.Publish(BuildCompletion(parentTraceId, parentRoundId, parentStartId, "kickoff", 1,
                "Completed", "file:///tmp/parent-start-out.bin"));

            // Drive 3 rounds: reject, reject, approve (so loop exits cleanly via Approved).
            await DriveReviewLoopRoundsAsync(harness, sagaHarness, parentTraceId, childStartId,
                terminalPorts: new[] { "Rejected", "Rejected", "Approved" });

            await SpinUntilAsync(() =>
            {
                var s = sagaHarness.Sagas.Contains(parentTraceId);
                return s is not null
                    && (s.CurrentState == nameof(WorkflowSagaStateMachine.Completed)
                        || s.CurrentState == nameof(WorkflowSagaStateMachine.Failed));
            });

            var parentSaga = sagaHarness.Sagas.Contains(parentTraceId)!;
            var workflowBag = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(parentSaga.WorkflowInputsJson)!;
            workflowBag.Should().ContainKey("boundaryInputCount");
            workflowBag["boundaryInputCount"].GetInt32().Should().Be(1,
                "boundary input script must fire exactly once across the entire ReviewLoop activation, not per iteration");
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task ReviewLoopOutputScript_RunsExactlyOnce_AfterLoopTerminates()
    {
        // ReviewLoop maxRounds=3 with an output script that increments a counter and asserts on
        // the final port. Drive reject, reject, approve. Assert: counter = 1 (output script
        // ran once, after the loop fully exited via Approved).
        var parentTraceId = Guid.NewGuid();
        var parentRoundId = Guid.NewGuid();
        var parentStartId = Guid.NewGuid();
        var parentLoopId = Guid.NewGuid();
        var childStartId = Guid.NewGuid();

        const string outputScript = """
            var n = workflow.boundaryOutputCount || 0;
            setWorkflow('boundaryOutputCount', n + 1);
            setWorkflow('finalDecision', output.decision);
            """;

        var parent = new Workflow(
            Key: "boundary-rl-output-parent",
            Version: 1,
            Name: "boundary-rl-output-parent",
            MaxRoundsPerRound: 5,
            CreatedAtUtc: DateTime.UtcNow,
            Nodes: new[]
            {
                new WorkflowNode(parentStartId, WorkflowNodeKind.Start, "kickoff", AgentVersion: 1,
                    OutputScript: null, OutputPorts: AllPorts, LayoutX: 0, LayoutY: 0),
                new WorkflowNode(parentLoopId, WorkflowNodeKind.ReviewLoop, AgentKey: null, AgentVersion: null,
                    OutputScript: outputScript, OutputPorts: new[] { "Approved", "Failed" },
                    LayoutX: 250, LayoutY: 0,
                    SubflowKey: "boundary-rl-output-child", SubflowVersion: 1,
                    ReviewMaxRounds: 3,
                    LoopDecision: "Rejected"),
            },
            Edges: new[]
            {
                new WorkflowEdge(parentStartId, "Completed", parentLoopId,
                    WorkflowEdge.DefaultInputPort, false, 0),
            },
            Inputs: Array.Empty<WorkflowInput>());

        var child = new Workflow(
            Key: "boundary-rl-output-child",
            Version: 1,
            Name: "boundary-rl-output-child",
            MaxRoundsPerRound: 5,
            CreatedAtUtc: DateTime.UtcNow,
            Nodes: new[]
            {
                new WorkflowNode(childStartId, WorkflowNodeKind.Start, "reviewer", AgentVersion: 1,
                    OutputScript: null, OutputPorts: AllPorts, LayoutX: 0, LayoutY: 0),
            },
            Edges: Array.Empty<WorkflowEdge>(),
            Inputs: Array.Empty<WorkflowInput>());

        var artifactStore = new RecordingArtifactStore();

        await using var scope = BuildHarness(new[] { parent, child },
            new Dictionary<string, int> { ["kickoff"] = 1, ["reviewer"] = 1 },
            artifactStore);
        var harness = scope.Harness;
        await harness.Start();
        try
        {
            await harness.Bus.Publish(new AgentInvokeRequested(
                TraceId: parentTraceId,
                RoundId: parentRoundId,
                WorkflowKey: parent.Key,
                WorkflowVersion: parent.Version,
                NodeId: parentStartId,
                AgentKey: "kickoff",
                AgentVersion: 1,
                InputRef: new Uri("file:///tmp/parent-in.bin"),
                ContextInputs: new Dictionary<string, JsonElement>()));

            var sagaHarness = harness.GetSagaStateMachineHarness<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
            await sagaHarness.Exists(parentTraceId, x => x.Running);

            await harness.Bus.Publish(BuildCompletion(parentTraceId, parentRoundId, parentStartId, "kickoff", 1,
                "Completed", "file:///tmp/parent-start-out.bin"));

            await DriveReviewLoopRoundsAsync(harness, sagaHarness, parentTraceId, childStartId,
                terminalPorts: new[] { "Rejected", "Rejected", "Approved" });

            await SpinUntilAsync(() =>
            {
                var s = sagaHarness.Sagas.Contains(parentTraceId);
                return s is not null
                    && (s.CurrentState == nameof(WorkflowSagaStateMachine.Completed)
                        || s.CurrentState == nameof(WorkflowSagaStateMachine.Failed));
            });

            var parentSaga = sagaHarness.Sagas.Contains(parentTraceId)!;
            var workflowBag = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(parentSaga.WorkflowInputsJson)!;
            workflowBag.Should().ContainKey("boundaryOutputCount");
            workflowBag["boundaryOutputCount"].GetInt32().Should().Be(1,
                "boundary output script must fire exactly once after the loop terminates, never per iteration");
            workflowBag.Should().ContainKey("finalDecision");
            workflowBag["finalDecision"].GetString().Should().Be("Approved",
                "output script must see the loop's exit port (Approved) on output.decision");
        }
        finally
        {
            await harness.Stop();
        }
    }

    /// <summary>
    /// Drives a ReviewLoop through a sequence of round terminations. For each port in
    /// <paramref name="terminalPorts"/>, waits for the next child saga to spin up, captures its
    /// AgentInvokeRequested, and publishes an AgentInvocationCompleted carrying that port. Each
    /// rejection re-spawns the child; the first non-loopDecision exits the loop.
    /// </summary>
    private static async Task DriveReviewLoopRoundsAsync(
        ITestHarness harness,
        ISagaStateMachineTestHarness<WorkflowSagaStateMachine, WorkflowSagaStateEntity> sagaHarness,
        Guid parentTraceId,
        Guid childStartId,
        IReadOnlyList<string> terminalPorts)
    {
        var seenChildTraces = new HashSet<Guid>();

        for (var round = 0; round < terminalPorts.Count; round++)
        {
            // Wait for the (round+1)-th SubflowInvokeRequested.
            var spawns = await WaitForPublishedAsync<SubflowInvokeRequested>(harness, round + 1);
            var spawn = spawns[round].Context.Message;
            seenChildTraces.Add(spawn.ChildTraceId);

            await sagaHarness.Exists(spawn.ChildTraceId, x => x.Running);

            // Find the child's first AgentInvokeRequested (the one for child.Start in this trace).
            AgentInvokeRequested? childInvoke = null;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < deadline && childInvoke is null)
            {
                childInvoke = harness.Published.Select<AgentInvokeRequested>()
                    .Select(m => m.Context.Message)
                    .FirstOrDefault(m => m.NodeId == childStartId && m.TraceId == spawn.ChildTraceId);
                if (childInvoke is null) await Task.Delay(25);
            }
            childInvoke.Should().NotBeNull("child saga's first AgentInvokeRequested must be published");

            // Publish completion on the chosen port for this round.
            await harness.Bus.Publish(BuildCompletion(spawn.ChildTraceId, childInvoke!.RoundId, childStartId,
                "reviewer", 1, terminalPorts[round], $"file:///tmp/round-{round + 1}-out.bin"));

            await SpinUntilAsync(() =>
            {
                var s = sagaHarness.Sagas.Contains(spawn.ChildTraceId);
                return s is not null
                    && (s.CurrentState == nameof(WorkflowSagaStateMachine.Completed)
                        || s.CurrentState == nameof(WorkflowSagaStateMachine.Failed));
            });
        }
    }

    private static async Task SpinUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(25);
        }
        throw new TimeoutException("Predicate not satisfied within timeout.");
    }

    private static async Task<IReadOnlyList<IPublishedMessage<T>>> WaitForPublishedAsync<T>(
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

    private static AgentInvocationCompleted BuildCompletion(
        Guid traceId,
        Guid roundId,
        Guid fromNodeId,
        string agentKey,
        int agentVersion,
        string decision,
        string outputRef)
    {
        return new AgentInvocationCompleted(
            TraceId: traceId,
            RoundId: roundId,
            FromNodeId: fromNodeId,
            AgentKey: agentKey,
            AgentVersion: agentVersion,
            OutputPortName: decision,
            OutputRef: new Uri(outputRef),
            DecisionPayload: JsonDocument.Parse($"{{\"portName\":\"{decision}\"}}").RootElement.Clone(),
            Duration: TimeSpan.FromMilliseconds(1),
            TokenUsage: new TokenUsage(0, 0, 0));
    }

    private static HarnessScope BuildHarness(
        IEnumerable<Workflow> workflows,
        IReadOnlyDictionary<string, int> agentVersions,
        IArtifactStore artifactStore)
    {
        var provider = new ServiceCollection()
            .AddSingleton<IWorkflowRepository>(new MultiWorkflowRepository(workflows))
            .AddSingleton<IAgentConfigRepository>(new StubAgentConfigRepository(agentVersions))
            .AddSingleton<IArtifactStore>(artifactStore)
            .AddSingleton<IMemoryCache>(_ => new MemoryCache(new MemoryCacheOptions()))
            .AddSingleton<LogicNodeScriptHost>()
            .AddSingleton<CodeFlow.Runtime.IScribanTemplateRenderer, CodeFlow.Runtime.ScribanTemplateRenderer>()
            .AddSingleton<IDecisionTemplateRenderer, DecisionTemplateRenderer>()
            .AddSingleton<IRetryContextBuilder, RetryContextBuilder>()
            .AddMassTransitTestHarness(x =>
            {
                x.AddSagaStateMachine<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
            })
            .BuildServiceProvider(true);

        return new HarnessScope(provider, provider.GetRequiredService<ITestHarness>());
    }

    private sealed class HarnessScope : IAsyncDisposable
    {
        private readonly ServiceProvider provider;

        public HarnessScope(ServiceProvider provider, ITestHarness harness)
        {
            this.provider = provider;
            Harness = harness;
        }

        public ITestHarness Harness { get; }

        public ValueTask DisposeAsync() => provider.DisposeAsync();
    }

    private sealed class MultiWorkflowRepository : IWorkflowRepository
    {
        private readonly Dictionary<string, Workflow> byKey;

        public MultiWorkflowRepository(IEnumerable<Workflow> workflows)
        {
            byKey = workflows.ToDictionary(w => w.Key, StringComparer.Ordinal);
        }

        public Task<Workflow> GetAsync(string key, int version, CancellationToken cancellationToken = default) =>
            byKey.TryGetValue(key, out var wf)
                ? Task.FromResult(wf)
                : throw new WorkflowNotFoundException(key, version);

        public Task<Workflow?> GetLatestAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(byKey.TryGetValue(key, out var wf) ? wf : null);

        public Task<IReadOnlyList<Workflow>> ListLatestAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Workflow>>(byKey.Values.ToArray());

        public Task<IReadOnlyList<Workflow>> ListVersionsAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Workflow>>(byKey.TryGetValue(key, out var wf) ? new[] { wf } : Array.Empty<Workflow>());

        public Task<WorkflowEdge?> FindNextAsync(
            string key,
            int version,
            Guid fromNodeId,
            string outputPortName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(byKey.TryGetValue(key, out var wf)
                ? wf.FindNext(fromNodeId, outputPortName)
                : null);

        public Task<IReadOnlyCollection<string>> GetTerminalPortsAsync(
            string key,
            int version,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(byKey.TryGetValue(key, out var wf)
                ? wf.TerminalPorts
                : (IReadOnlyCollection<string>)Array.Empty<string>());

        public Task<int> CreateNewVersionAsync(WorkflowDraft draft, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubAgentConfigRepository : IAgentConfigRepository
    {
        private readonly IReadOnlyDictionary<string, int> versions;

        public StubAgentConfigRepository(IReadOnlyDictionary<string, int> versions)
        {
            this.versions = versions;
        }

        public Task<AgentConfig> GetAsync(string key, int version, CancellationToken cancellationToken = default)
        {
            var empty = new AgentConfig(
                Key: key,
                Version: version,
                Kind: AgentKind.Agent,
                Configuration: new CodeFlow.Runtime.AgentInvocationConfiguration(
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
            versions.TryGetValue(key, out var v)
                ? Task.FromResult(v)
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

    /// <summary>
    /// Artifact store that records every write and serves seeded reads. Lets the boundary-script
    /// tests verify that setOutput/setInput overrides actually land in the store and propagate
    /// to downstream URIs.
    /// </summary>
    private sealed class RecordingArtifactStore : IArtifactStore
    {
        private readonly Dictionary<Uri, byte[]> writes = new();
        private readonly Dictionary<Uri, string> seeds = new();

        public void SeedRead(Uri uri, string content) => seeds[uri] = content;

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
            if (seeds.TryGetValue(uri, out var seeded))
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
}
