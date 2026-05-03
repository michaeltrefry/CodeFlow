using CodeFlow.Contracts;
using CodeFlow.Orchestration.Scripting;
using CodeFlow.Persistence;
using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace CodeFlow.Orchestration.Tests;

/// <summary>
/// S12: end-to-end coverage of the subflow round-trip purely through the saga state machine,
/// driving the AgentInvocationCompleted events by hand so the test doesn't need the full
/// AgentInvocationConsumer pipeline (which would require a live DB for HITL task persistence).
/// </summary>
[Collection("Bus integration")]
public sealed class WorkflowSagaSubflowEndToEndTests
{
    private static readonly IReadOnlyList<string> AllDecisionPorts =
        new[] { "Completed", "Approved", "Rejected", "Failed" };

    [Fact]
    public async Task ParentToChild_HappyPath_ShouldRoundTripAndMergeGlobal()
    {
        // Parent: Start(kickoff) → [Completed] → Subflow(child-flow v1) with no outgoing edges
        //   from the Subflow node. When the child reaches Completed, the parent's Subflow.Completed
        //   port has no edge, so the parent terminates Completed itself.
        // Child: Start(child-agent) with no outgoing edges — Completed terminal.
        var parentTraceId = Guid.NewGuid();
        var parentRoundId = Guid.NewGuid();

        var parentStartNodeId = Guid.NewGuid();
        var parentSubflowNodeId = Guid.NewGuid();
        var parent = new Workflow(
            Key: "parent-e2e",
            Version: 1,
            Name: "parent-e2e",
            MaxRoundsPerRound: 5,
            CreatedAtUtc: DateTime.UtcNow,
            Nodes: new[]
            {
                new WorkflowNode(parentStartNodeId, WorkflowNodeKind.Start, "kickoff",
                    AgentVersion: null, OutputScript: null, OutputPorts: AllDecisionPorts, LayoutX: 0, LayoutY: 0),
                new WorkflowNode(parentSubflowNodeId, WorkflowNodeKind.Subflow, AgentKey: null,
                    AgentVersion: null, OutputScript: null,
                    OutputPorts: new[] { "Completed", "Failed", "Escalated" },
                    LayoutX: 250, LayoutY: 0,
                    SubflowKey: "child-e2e", SubflowVersion: 1),
            },
            Edges: new[]
            {
                new WorkflowEdge(parentStartNodeId, "Completed", parentSubflowNodeId,
                    WorkflowEdge.DefaultInputPort, false, 0),
            },
            Inputs: Array.Empty<WorkflowInput>());

        var childStartNodeId = Guid.NewGuid();
        var child = new Workflow(
            Key: "child-e2e",
            Version: 1,
            Name: "child-e2e",
            MaxRoundsPerRound: 5,
            CreatedAtUtc: DateTime.UtcNow,
            Nodes: new[]
            {
                new WorkflowNode(childStartNodeId, WorkflowNodeKind.Start, "child-agent",
                    AgentVersion: null, OutputScript: null, OutputPorts: AllDecisionPorts, LayoutX: 0, LayoutY: 0),
            },
            Edges: Array.Empty<WorkflowEdge>(),
            Inputs: Array.Empty<WorkflowInput>());

        await using var scope = BuildHarness(new[] { parent, child }, new Dictionary<string, int>
        {
            ["kickoff"] = 1,
            ["child-agent"] = 1,
        });
        var harness = scope.Harness;

        await harness.Start();
        try
        {
            // 1. Kick off the parent trace on its Start node.
            await harness.Bus.Publish(new AgentInvokeRequested(
                TraceId: parentTraceId,
                RoundId: parentRoundId,
                WorkflowKey: parent.Key,
                WorkflowVersion: parent.Version,
                NodeId: parentStartNodeId,
                AgentKey: "kickoff",
                AgentVersion: 1,
                InputRef: new Uri("file:///tmp/parent-in.bin"),
                ContextInputs: new Dictionary<string, JsonElement>()));

            var sagaHarness = harness.GetSagaStateMachineHarness<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
            await sagaHarness.Exists(parentTraceId, x => x.Running);

            // 2. Parent Start completes → saga routes to Subflow node → publishes SubflowInvokeRequested.
            await harness.Bus.Publish(BuildCompletion(parentTraceId, parentRoundId, parentStartNodeId, "kickoff", 1,
                "Completed", "file:///tmp/parent-start-out.bin"));

            var spawns = await WaitForPublishedAsync<SubflowInvokeRequested>(harness, 1);
            spawns.Should().ContainSingle();
            var spawn = spawns[0].Context.Message;
            spawn.SubflowKey.Should().Be("child-e2e");
            spawn.Depth.Should().Be(1);
            var childTraceId = spawn.ChildTraceId;

            // 3. Child saga gets created with parent linkage.
            await sagaHarness.Exists(childTraceId, x => x.Running);

            // 4. Child saga's init published an AgentInvokeRequested for child.Start — but the parent
            //    Start also published one. Grab the one targeted at child.Start.
            var childInvocations = await WaitForPublishedAsync<AgentInvokeRequested>(harness,
                expectedCount: 2); // parent Start + child Start
            var childInvoke = childInvocations
                .Select(m => m.Context.Message)
                .Single(m => m.NodeId == childStartNodeId && m.TraceId == childTraceId);
            childInvoke.AgentKey.Should().Be("child-agent");

            // 5. Simulate child.Start completing → child saga terminates Completed (no edges).
            await harness.Bus.Publish(BuildCompletion(childTraceId, childInvoke.RoundId, childStartNodeId,
                "child-agent", 1, "Completed", "file:///tmp/child-final.bin"));

            await sagaHarness.Exists(childTraceId, x => x.Completed);

            // 6. Child's WhenEnter(Completed) publishes SubflowCompleted → parent consumes → routes
            //    from Subflow.Completed port (no edge) → parent terminates Completed.
            var subflowCompletions = await WaitForPublishedAsync<SubflowCompleted>(harness, 1);
            var subflowCompleted = subflowCompletions[0].Context.Message;
            subflowCompleted.ParentTraceId.Should().Be(parentTraceId);
            subflowCompleted.ParentNodeId.Should().Be(parentSubflowNodeId);
            subflowCompleted.OutputPortName.Should().Be("Completed");
            subflowCompleted.ChildTraceId.Should().Be(childTraceId);

            await sagaHarness.Exists(parentTraceId, x => x.Completed);

            var parentSaga = sagaHarness.Sagas.Contains(parentTraceId)!;
            parentSaga.CurrentState.Should().Be(nameof(WorkflowSagaStateMachine.Completed));
            parentSaga.ParentTraceId.Should().BeNull("parent is top-level");

            var childSaga = sagaHarness.Sagas.Contains(childTraceId)!;
            childSaga.CurrentState.Should().Be(nameof(WorkflowSagaStateMachine.Completed));
            childSaga.ParentTraceId.Should().Be(parentTraceId);
            childSaga.SubflowDepth.Should().Be(1);
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task RepositoriesDeclaredOnParent_ShouldPropagateToChildSagaAndChildAgentDispatch()
    {
        // Cross-saga propagation contract: a `repositories` allowlist set on the parent's
        // ContextInputs at launch must reach a subflow's vcs_* tools without each child
        // workflow having to redeclare it. The saga lifts the entry to RepositoriesJson, the
        // dispatcher passes it through SubflowInvokeRequested.Repositories, and the child saga
        // seeds its own RepositoriesJson on init — so the child's first AgentInvokeRequested
        // carries the inherited allowlist.
        var parentTraceId = Guid.NewGuid();
        var parentRoundId = Guid.NewGuid();
        var parentStartNodeId = Guid.NewGuid();
        var parentSubflowNodeId = Guid.NewGuid();

        var parent = new Workflow(
            Key: "parent-repos",
            Version: 1,
            Name: "parent-repos",
            MaxRoundsPerRound: 5,
            CreatedAtUtc: DateTime.UtcNow,
            Nodes: new[]
            {
                new WorkflowNode(parentStartNodeId, WorkflowNodeKind.Start, "kickoff",
                    AgentVersion: null, OutputScript: null, OutputPorts: AllDecisionPorts, LayoutX: 0, LayoutY: 0),
                new WorkflowNode(parentSubflowNodeId, WorkflowNodeKind.Subflow, AgentKey: null,
                    AgentVersion: null, OutputScript: null,
                    OutputPorts: new[] { "Completed", "Failed", "Escalated" },
                    LayoutX: 250, LayoutY: 0,
                    SubflowKey: "child-repos", SubflowVersion: 1),
            },
            Edges: new[]
            {
                new WorkflowEdge(parentStartNodeId, "Completed", parentSubflowNodeId,
                    WorkflowEdge.DefaultInputPort, false, 0),
            },
            Inputs: Array.Empty<WorkflowInput>());

        var childStartNodeId = Guid.NewGuid();
        var child = new Workflow(
            Key: "child-repos",
            Version: 1,
            Name: "child-repos",
            MaxRoundsPerRound: 5,
            CreatedAtUtc: DateTime.UtcNow,
            Nodes: new[]
            {
                new WorkflowNode(childStartNodeId, WorkflowNodeKind.Start, "child-agent",
                    AgentVersion: null, OutputScript: null, OutputPorts: AllDecisionPorts, LayoutX: 0, LayoutY: 0),
            },
            Edges: Array.Empty<WorkflowEdge>(),
            Inputs: Array.Empty<WorkflowInput>());

        await using var scope = BuildHarness(new[] { parent, child }, new Dictionary<string, int>
        {
            ["kickoff"] = 1,
            ["child-agent"] = 1,
        });
        var harness = scope.Harness;

        await harness.Start();
        try
        {
            // Parent launches with `repositories` in context — the existing input convention.
            var contextInputs = new Dictionary<string, JsonElement>
            {
                ["repositories"] = JsonDocument.Parse(
                    "[{\"url\":\"https://github.com/acme/widget.git\",\"branch\":\"main\"}]")
                    .RootElement.Clone(),
            };

            await harness.Bus.Publish(new AgentInvokeRequested(
                TraceId: parentTraceId,
                RoundId: parentRoundId,
                WorkflowKey: parent.Key,
                WorkflowVersion: parent.Version,
                NodeId: parentStartNodeId,
                AgentKey: "kickoff",
                AgentVersion: 1,
                InputRef: new Uri("file:///tmp/parent-in.bin"),
                ContextInputs: contextInputs));

            var sagaHarness = harness.GetSagaStateMachineHarness<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
            await sagaHarness.Exists(parentTraceId, x => x.Running);

            var parentSaga = sagaHarness.Sagas.Contains(parentTraceId)!;
            parentSaga.RepositoriesJson.Should().NotBeNullOrWhiteSpace(
                "ApplyInitialRequest must lift context.repositories into saga.RepositoriesJson");
            parentSaga.RepositoriesJson!.Should().Contain("acme/widget");

            // Parent Start → Subflow dispatch. The SubflowInvokeRequested must carry the parent's
            // allowlist on its Repositories field.
            await harness.Bus.Publish(BuildCompletion(parentTraceId, parentRoundId, parentStartNodeId,
                "kickoff", 1, "Completed", "file:///tmp/parent-start-out.bin"));

            var spawns = await WaitForPublishedAsync<SubflowInvokeRequested>(harness, 1);
            spawns.Should().ContainSingle();
            var spawn = spawns[0].Context.Message;
            spawn.Repositories.Should().NotBeNull(
                "PublishSubflowDispatchAsync must thread the parent's allowlist onto SubflowInvokeRequested.Repositories");
            spawn.Repositories!.Should().ContainSingle()
                .Which.Url.Should().Be("https://github.com/acme/widget.git");
            spawn.Repositories[0].Branch.Should().Be("main");

            var childTraceId = spawn.ChildTraceId;
            await sagaHarness.Exists(childTraceId, x => x.Running);

            // Child saga inherits the parent's allowlist verbatim on init.
            var childSaga = sagaHarness.Sagas.Contains(childTraceId)!;
            childSaga.RepositoriesJson.Should().NotBeNullOrWhiteSpace(
                "ApplyInitialSubflowAsync must seed child.RepositoriesJson from message.Repositories");
            childSaga.RepositoriesJson!.Should().Contain("acme/widget");

            // The first dispatch to the child's Start agent carries the inherited Repositories
            // — which is what the runtime BuildToolExecutionContext consults when checking the
            // vcs_* allowlist.
            var childDispatch = await WaitForAgentInvocationAsync(harness, childTraceId, childStartNodeId);
            childDispatch.Repositories.Should().NotBeNull(
                "child Start dispatch must carry the inherited per-trace allowlist on Repositories");
            childDispatch.Repositories!.Should().ContainSingle()
                .Which.Url.Should().Be("https://github.com/acme/widget.git");
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task FourLevelChain_ShouldFailDeepestWithDepthExceededAndBubbleFailedToRoot()
    {
        // Chain: root → A → B → C (C is at depth 3, the deepest legal child). C has a Subflow
        // node pointing at D — spawning D would land at depth 4 and be rejected. C fails with
        // SubflowDepthExceeded, and the Failed bubbles all the way up to root.
        // Each intermediate workflow has NO outgoing edge from its Subflow.Failed port, so the
        // saga terminates Failed on each level.
        var workflows = new List<Workflow>();
        var startNodeIds = new Dictionary<string, Guid>();
        var subflowNodeIds = new Dictionary<string, Guid>();

        for (var i = 0; i < 4; i++)
        {
            var key = $"chain-{(char)('A' + i)}"; // chain-A, chain-B, chain-C, chain-D
            var next = i < 3 ? $"chain-{(char)('A' + i + 1)}" : "chain-leaf"; // chain-D tries to spawn chain-leaf
            var startId = Guid.NewGuid();
            var subflowId = Guid.NewGuid();
            startNodeIds[key] = startId;
            subflowNodeIds[key] = subflowId;

            workflows.Add(new Workflow(
                Key: key,
                Version: 1,
                Name: key,
                MaxRoundsPerRound: 5,
                CreatedAtUtc: DateTime.UtcNow,
                Nodes: new[]
                {
                    new WorkflowNode(startId, WorkflowNodeKind.Start, $"start-{key}",
                        AgentVersion: null, OutputScript: null, OutputPorts: AllDecisionPorts, 0, 0),
                    new WorkflowNode(subflowId, WorkflowNodeKind.Subflow, AgentKey: null,
                        AgentVersion: null, OutputScript: null,
                        OutputPorts: new[] { "Completed", "Failed", "Escalated" },
                        250, 0,
                        SubflowKey: next, SubflowVersion: 1),
                },
                Edges: new[]
                {
                    new WorkflowEdge(startId, "Completed", subflowId,
                        WorkflowEdge.DefaultInputPort, false, 0),
                },
                Inputs: Array.Empty<WorkflowInput>()));
        }

        // chain-leaf exists only to satisfy save-time validation hypothetically; we never reach it
        // because depth 4 is rejected before the spawn. But the workflow repo needs to be able to
        // resolve it if asked — we include a minimal stub.
        var leafStartId = Guid.NewGuid();
        workflows.Add(new Workflow(
            Key: "chain-leaf",
            Version: 1,
            Name: "chain-leaf",
            MaxRoundsPerRound: 5,
            CreatedAtUtc: DateTime.UtcNow,
            Nodes: new[]
            {
                new WorkflowNode(leafStartId, WorkflowNodeKind.Start, "start-leaf",
                    AgentVersion: null, OutputScript: null, OutputPorts: AllDecisionPorts, 0, 0),
            },
            Edges: Array.Empty<WorkflowEdge>(),
            Inputs: Array.Empty<WorkflowInput>()));

        var agentVersions = new Dictionary<string, int>
        {
            ["start-chain-A"] = 1,
            ["start-chain-B"] = 1,
            ["start-chain-C"] = 1,
            ["start-chain-D"] = 1,
            ["start-leaf"] = 1,
        };

        await using var scope = BuildHarness(workflows, agentVersions);
        var harness = scope.Harness;
        await harness.Start();
        try
        {
            var rootTraceId = Guid.NewGuid();
            var rootRoundId = Guid.NewGuid();

            await harness.Bus.Publish(new AgentInvokeRequested(
                TraceId: rootTraceId,
                RoundId: rootRoundId,
                WorkflowKey: "chain-A",
                WorkflowVersion: 1,
                NodeId: startNodeIds["chain-A"],
                AgentKey: "start-chain-A",
                AgentVersion: 1,
                InputRef: new Uri("file:///tmp/root-in.bin"),
                ContextInputs: new Dictionary<string, JsonElement>()));

            var sagaHarness = harness.GetSagaStateMachineHarness<WorkflowSagaStateMachine, WorkflowSagaStateEntity>();
            await sagaHarness.Exists(rootTraceId, x => x.Running);

            // Drive the chain by completing each Start and letting each saga spawn the next.
            await CompleteStartAndSpawnNext(harness, rootTraceId, rootRoundId, "chain-A", startNodeIds["chain-A"]);

            // For A: spawns B (depth 1). For B: spawns C (depth 2). For C: spawns D (depth 3, legal).
            // For D: tries to spawn leaf at depth 4 → rejected. Simulate that by completing each Start.
            // Grab the ChildTraceId of the most recent SubflowInvokeRequested to know who to drive next.
            var chainChain = new List<(Guid traceId, Guid roundId, string key)>
            {
                (rootTraceId, rootRoundId, "chain-A"),
            };

            // A → spawns B
            var aToB = await WaitForPublishedAsync<SubflowInvokeRequested>(harness, 1);
            aToB.Should().ContainSingle();
            var bTrace = aToB[0].Context.Message.ChildTraceId;
            var bStartInvoke = await WaitForAgentInvocationAsync(harness, bTrace, startNodeIds["chain-B"]);
            chainChain.Add((bTrace, bStartInvoke.RoundId, "chain-B"));
            await CompleteStartAndSpawnNext(harness, bTrace, bStartInvoke.RoundId, "chain-B", startNodeIds["chain-B"]);

            // B → spawns C
            var bToC = await WaitForPublishedAsync<SubflowInvokeRequested>(harness, 2);
            var cSpawn = bToC.Last().Context.Message;
            cSpawn.Depth.Should().Be(2);
            var cTrace = cSpawn.ChildTraceId;
            var cStartInvoke = await WaitForAgentInvocationAsync(harness, cTrace, startNodeIds["chain-C"]);
            chainChain.Add((cTrace, cStartInvoke.RoundId, "chain-C"));
            await CompleteStartAndSpawnNext(harness, cTrace, cStartInvoke.RoundId, "chain-C", startNodeIds["chain-C"]);

            // C → spawns D at depth 3 (legal).
            var cToD = await WaitForPublishedAsync<SubflowInvokeRequested>(harness, 3);
            var dSpawn = cToD.Last().Context.Message;
            dSpawn.Depth.Should().Be(3);
            var dTrace = dSpawn.ChildTraceId;
            var dStartInvoke = await WaitForAgentInvocationAsync(harness, dTrace, startNodeIds["chain-D"]);
            chainChain.Add((dTrace, dStartInvoke.RoundId, "chain-D"));

            // D.Start completes → D tries to spawn chain-leaf at depth 4 → DEPTH CAP REJECT.
            await harness.Bus.Publish(BuildCompletion(dTrace, dStartInvoke.RoundId, startNodeIds["chain-D"],
                "start-chain-D", 1, "Completed", "file:///tmp/d-out.bin"));

            // D saga should transition to Failed with SubflowDepthExceeded.
            await sagaHarness.Exists(dTrace, x => x.Failed);
            var dSaga = sagaHarness.Sagas.Contains(dTrace)!;
            dSaga.FailureReason.Should().NotBeNullOrWhiteSpace();
            dSaga.FailureReason!.Should().Contain("SubflowDepthExceeded");

            // D publishes SubflowCompleted with port "Failed" → C routes from Subflow.Failed port
            // (no edge) → C terminates Failed. Same happens at B and A.
            await sagaHarness.Exists(cTrace, x => x.Failed);
            await sagaHarness.Exists(bTrace, x => x.Failed);
            await sagaHarness.Exists(rootTraceId, x => x.Failed);

            var rootSaga = sagaHarness.Sagas.Contains(rootTraceId)!;
            rootSaga.CurrentState.Should().Be(nameof(WorkflowSagaStateMachine.Failed));
            rootSaga.FailureReason.Should().NotBeNullOrWhiteSpace();

            // Should NOT have published a fourth SubflowInvokeRequested — the depth cap fires
            // BEFORE the spawn.
            var allSpawns = harness.Published.Select<SubflowInvokeRequested>().ToList();
            allSpawns.Should().HaveCount(3, "depth cap prevents the 4th spawn");
        }
        finally
        {
            await harness.Stop();
        }
    }

    private static async Task CompleteStartAndSpawnNext(
        ITestHarness harness,
        Guid traceId,
        Guid roundId,
        string workflowKey,
        Guid startNodeId)
    {
        await harness.Bus.Publish(BuildCompletion(traceId, roundId, startNodeId,
            $"start-{workflowKey}", 1, "Completed",
            $"file:///tmp/{workflowKey}-out.bin"));
    }

    private static async Task<AgentInvokeRequested> WaitForAgentInvocationAsync(
        ITestHarness harness,
        Guid traceId,
        Guid nodeId,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTime.UtcNow < deadline)
        {
            var match = harness.Published.Select<AgentInvokeRequested>()
                .Select(m => m.Context.Message)
                .FirstOrDefault(m => m.TraceId == traceId && m.NodeId == nodeId);
            if (match is not null)
            {
                return match;
            }
            await Task.Delay(25);
        }
        throw new TimeoutException(
            $"No AgentInvokeRequested published for trace {traceId} node {nodeId} within the timeout.");
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
        IReadOnlyDictionary<string, int> agentVersions)
    {
        var provider = new ServiceCollection()
            .AddSingleton<IWorkflowRepository>(new MultiWorkflowRepository(workflows))
            .AddSingleton<IAgentConfigRepository>(new StubAgentConfigRepository(agentVersions))
            .AddSingleton<IArtifactStore>(new DictionaryArtifactStore())
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

    // Wraps the harness + its provider so the test can dispose both. Without disposing the
    // provider, the MassTransit hosted services keep running after the test ends, and stale
    // publish-topology state leaks into the RabbitMQ-backed E2E test that runs next in the
    // collection (producing a saga-never-initializes timeout).
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

    private sealed class DictionaryArtifactStore : IArtifactStore
    {
        public Task<ArtifactMetadata> GetMetadataAsync(Uri uri, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Stream> ReadAsync(Uri uri, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{}")));

        public Task<Uri> WriteAsync(Stream content, ArtifactMetadata metadata, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
