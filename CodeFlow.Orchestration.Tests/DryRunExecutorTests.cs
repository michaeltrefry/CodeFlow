using System.Text.Json;
using System.Text.Json.Nodes;
using CodeFlow.Orchestration.DryRun;
using CodeFlow.Orchestration.Scripting;
using CodeFlow.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;

namespace CodeFlow.Orchestration.Tests;

public sealed class DryRunExecutorTests
{
    private static readonly Guid OuterStartId = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ReviewLoopId = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid InnerStartId = new("33333333-3333-3333-3333-333333333333");
    private static readonly Guid ProducerId = new("44444444-4444-4444-4444-444444444444");
    private static readonly Guid ReviewerId = new("55555555-5555-5555-5555-555555555555");

    /// <summary>
    /// Acceptance criterion: "Reviewer mock decision = Approved exits cleanly."
    /// Producer fires once, reviewer approves on round 1, the outer workflow terminates Approved.
    /// </summary>
    [Fact]
    public async Task ReviewLoopPair_ApprovedFirstRound_ExitsCleanly()
    {
        var (outer, inner) = BuildReviewLoopPair(maxRounds: 3);
        var repo = new MultiWorkflowFakeRepository(outer, inner);
        var executor = new DryRunExecutor(repo, new LogicNodeScriptHost(new MemoryCache(new MemoryCacheOptions())));

        var mocks = new Dictionary<string, IReadOnlyList<DryRunMockResponse>>
        {
            ["producer"] = new[] { new DryRunMockResponse("Completed", "draft v1", null) },
            ["reviewer"] = new[] { new DryRunMockResponse("Approved", "looks good", null) },
        };

        var result = await executor.ExecuteAsync(
            new DryRunRequest("outer", null, "initial PRD", mocks),
            CancellationToken.None);

        result.State.Should().Be(DryRunTerminalState.Completed);
        result.TerminalPort.Should().Be("Approved");
        result.Events.Should().Contain(e => e.Kind == DryRunEventKind.LoopIteration && e.ReviewRound == 1);
        result.Events.Should().NotContain(e => e.Kind == DryRunEventKind.LoopIteration && e.ReviewRound == 2);
    }

    /// <summary>
    /// Acceptance criterion: "Rejected iterates."
    /// Reviewer rejects rounds 1-2, approves round 3 — workflow terminates Approved on round 3.
    /// </summary>
    [Fact]
    public async Task ReviewLoopPair_RejectedThenApproved_IteratesUntilApproval()
    {
        var (outer, inner) = BuildReviewLoopPair(maxRounds: 5);
        var repo = new MultiWorkflowFakeRepository(outer, inner);
        var executor = new DryRunExecutor(repo, new LogicNodeScriptHost(new MemoryCache(new MemoryCacheOptions())));

        var mocks = new Dictionary<string, IReadOnlyList<DryRunMockResponse>>
        {
            ["producer"] = new[]
            {
                new DryRunMockResponse("Completed", "draft v1", null),
                new DryRunMockResponse("Completed", "draft v2", null),
                new DryRunMockResponse("Completed", "draft v3", null),
            },
            ["reviewer"] = new[]
            {
                new DryRunMockResponse("Rejected", "needs better citations", null),
                new DryRunMockResponse("Rejected", "still missing context", null),
                new DryRunMockResponse("Approved", "ship it", null),
            },
        };

        var result = await executor.ExecuteAsync(
            new DryRunRequest("outer", null, "initial PRD", mocks),
            CancellationToken.None);

        result.State.Should().Be(DryRunTerminalState.Completed);
        result.TerminalPort.Should().Be("Approved");
        var loopIterations = result.Events.Count(e => e.Kind == DryRunEventKind.LoopIteration);
        loopIterations.Should().Be(3);
    }

    /// <summary>
    /// All rounds reject → loop emits the synthetic Exhausted port.
    /// </summary>
    [Fact]
    public async Task ReviewLoopPair_AllRejected_ExhaustsLoop()
    {
        var (outer, inner) = BuildReviewLoopPair(maxRounds: 2);
        var repo = new MultiWorkflowFakeRepository(outer, inner);
        var executor = new DryRunExecutor(repo, new LogicNodeScriptHost(new MemoryCache(new MemoryCacheOptions())));

        var mocks = new Dictionary<string, IReadOnlyList<DryRunMockResponse>>
        {
            ["producer"] = new[]
            {
                new DryRunMockResponse("Completed", "draft v1", null),
                new DryRunMockResponse("Completed", "draft v2", null),
            },
            ["reviewer"] = new[]
            {
                new DryRunMockResponse("Rejected", "no", null),
                new DryRunMockResponse("Rejected", "still no", null),
            },
        };

        var result = await executor.ExecuteAsync(
            new DryRunRequest("outer", null, "initial PRD", mocks),
            CancellationToken.None);

        result.State.Should().Be(DryRunTerminalState.Completed);
        result.TerminalPort.Should().Be("Exhausted");
        result.Events.Should().Contain(e => e.Kind == DryRunEventKind.LoopExhausted);
    }

    /// <summary>
    /// HITL inside the workflow halts the dry-run with the form payload captured.
    /// </summary>
    [Fact]
    public async Task HitlNode_HaltsExecutionWithPayloadCaptured()
    {
        var workflow = BuildWorkflowWithHitl();
        var repo = new MultiWorkflowFakeRepository(workflow);
        var executor = new DryRunExecutor(repo, new LogicNodeScriptHost(new MemoryCache(new MemoryCacheOptions())));

        var mocks = new Dictionary<string, IReadOnlyList<DryRunMockResponse>>
        {
            ["producer"] = new[] { new DryRunMockResponse("Completed", "ready for review", null) },
        };

        var result = await executor.ExecuteAsync(
            new DryRunRequest("hitl-flow", null, "input", mocks),
            CancellationToken.None);

        result.State.Should().Be(DryRunTerminalState.HitlReached);
        result.HitlPayload.Should().NotBeNull();
        result.HitlPayload!.AgentKey.Should().Be("hitl-approver");
        result.HitlPayload.Input.Should().Be("ready for review");
    }

    /// <summary>
    /// P4 mirror + P5 replacement built-ins fire correctly.
    /// </summary>
    [Fact]
    public async Task P4_AndP5_BuiltinsAppliedDuringDryRun()
    {
        var workflow = BuildWorkflowWithMirrorAndReplacement();
        var repo = new MultiWorkflowFakeRepository(workflow);
        var executor = new DryRunExecutor(repo, new LogicNodeScriptHost(new MemoryCache(new MemoryCacheOptions())));

        var mocks = new Dictionary<string, IReadOnlyList<DryRunMockResponse>>
        {
            ["producer"] = new[] { new DryRunMockResponse("Completed", "the captured plan", null) },
            ["reviewer"] = new[] { new DryRunMockResponse("Approved", "ack", null) },
        };

        var result = await executor.ExecuteAsync(
            new DryRunRequest("mirror-flow", null, "seed", mocks),
            CancellationToken.None);

        result.State.Should().Be(DryRunTerminalState.Completed);
        result.WorkflowVariables.Should().ContainKey("currentPlan");
        result.WorkflowVariables["currentPlan"].GetString().Should().Be("the captured plan");
        result.FinalArtifact.Should().Be("the captured plan",
            because: "P5 replaces the reviewer's `ack` with the workflow.currentPlan value on Approved port.");
    }

    /// <summary>
    /// Logic node with a script that calls setNodePath routes deterministically.
    /// </summary>
    [Fact]
    public async Task LogicNode_RoutesViaScriptedSetNodePath()
    {
        var workflow = BuildWorkflowWithLogicNode();
        var repo = new MultiWorkflowFakeRepository(workflow);
        var executor = new DryRunExecutor(repo, new LogicNodeScriptHost(new MemoryCache(new MemoryCacheOptions())));

        var mocks = new Dictionary<string, IReadOnlyList<DryRunMockResponse>>
        {
            ["left-agent"] = new[] { new DryRunMockResponse("LeftCompleted", "took the left fork", null) },
        };

        var result = await executor.ExecuteAsync(
            new DryRunRequest("logic-flow", null, "{\"path\":\"left\"}", mocks),
            CancellationToken.None);

        result.State.Should().Be(DryRunTerminalState.Completed);
        result.TerminalPort.Should().Be("LeftCompleted");
        result.Events.Should().Contain(e => e.Kind == DryRunEventKind.LogicEvaluated && e.PortName == "Left");
    }

    /// <summary>
    /// Missing mock for an agent surfaces a clear failure rather than a runtime exception.
    /// </summary>
    [Fact]
    public async Task MissingAgentMock_ProducesClearFailure()
    {
        var (outer, inner) = BuildReviewLoopPair(maxRounds: 2);
        var repo = new MultiWorkflowFakeRepository(outer, inner);
        var executor = new DryRunExecutor(repo, new LogicNodeScriptHost(new MemoryCache(new MemoryCacheOptions())));

        var mocks = new Dictionary<string, IReadOnlyList<DryRunMockResponse>>
        {
            // Reviewer queue is empty.
            ["producer"] = new[] { new DryRunMockResponse("Completed", "draft", null) },
        };

        var result = await executor.ExecuteAsync(
            new DryRunRequest("outer", null, "init", mocks),
            CancellationToken.None);

        result.State.Should().Be(DryRunTerminalState.Failed);
        result.FailureReason.Should().Contain("reviewer");
    }

    // ---------- workflow builders ----------

    private static (Workflow Outer, Workflow Inner) BuildReviewLoopPair(int maxRounds)
    {
        var inner = new Workflow(
            Key: "inner",
            Version: 1,
            Name: "Inner ReviewLoop",
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
                    OutputScript: null,
                    OutputPorts: new[] { "Approved", "Exhausted" },
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

    private static Workflow BuildWorkflowWithHitl()
    {
        var startId = Guid.Parse("aaaaaaaa-1111-aaaa-aaaa-aaaaaaaaaaaa");
        var producerId = Guid.Parse("aaaaaaaa-2222-aaaa-aaaa-aaaaaaaaaaaa");
        var hitlId = Guid.Parse("aaaaaaaa-3333-aaaa-aaaa-aaaaaaaaaaaa");

        return new Workflow(
            Key: "hitl-flow",
            Version: 1,
            Name: "HITL flow",
            MaxRoundsPerRound: 64,
            CreatedAtUtc: DateTime.UtcNow,
            Nodes: new[]
            {
                new WorkflowNode(
                    Id: startId, Kind: WorkflowNodeKind.Start, AgentKey: null, AgentVersion: null,
                    OutputScript: null, OutputPorts: new[] { "Completed" }, LayoutX: 0, LayoutY: 0),
                new WorkflowNode(
                    Id: producerId, Kind: WorkflowNodeKind.Agent, AgentKey: "producer", AgentVersion: 1,
                    OutputScript: null, OutputPorts: new[] { "Completed" }, LayoutX: 100, LayoutY: 0),
                new WorkflowNode(
                    Id: hitlId, Kind: WorkflowNodeKind.Hitl, AgentKey: "hitl-approver", AgentVersion: 1,
                    OutputScript: null, OutputPorts: new[] { "Approved" }, LayoutX: 200, LayoutY: 0),
            },
            Edges: new[]
            {
                new WorkflowEdge(startId, "Completed", producerId, "in", false, 0),
                new WorkflowEdge(producerId, "Completed", hitlId, "in", false, 0),
            },
            Inputs: Array.Empty<WorkflowInput>());
    }

    private static Workflow BuildWorkflowWithMirrorAndReplacement()
    {
        var startId = Guid.Parse("bbbbbbbb-1111-bbbb-bbbb-bbbbbbbbbbbb");
        var producerId = Guid.Parse("bbbbbbbb-2222-bbbb-bbbb-bbbbbbbbbbbb");
        var reviewerId = Guid.Parse("bbbbbbbb-3333-bbbb-bbbb-bbbbbbbbbbbb");

        return new Workflow(
            Key: "mirror-flow",
            Version: 1,
            Name: "Mirror + replace flow",
            MaxRoundsPerRound: 64,
            CreatedAtUtc: DateTime.UtcNow,
            Nodes: new[]
            {
                new WorkflowNode(
                    Id: startId, Kind: WorkflowNodeKind.Start, AgentKey: null, AgentVersion: null,
                    OutputScript: null, OutputPorts: new[] { "Completed" }, LayoutX: 0, LayoutY: 0),
                new WorkflowNode(
                    Id: producerId, Kind: WorkflowNodeKind.Agent, AgentKey: "producer", AgentVersion: 1,
                    OutputScript: null, OutputPorts: new[] { "Completed" }, LayoutX: 100, LayoutY: 0,
                    MirrorOutputToWorkflowVar: "currentPlan"),
                new WorkflowNode(
                    Id: reviewerId, Kind: WorkflowNodeKind.Agent, AgentKey: "reviewer", AgentVersion: 1,
                    OutputScript: null, OutputPorts: new[] { "Approved", "Rejected" }, LayoutX: 200, LayoutY: 0,
                    OutputPortReplacements: new Dictionary<string, string> { ["Approved"] = "currentPlan" }),
            },
            Edges: new[]
            {
                new WorkflowEdge(startId, "Completed", producerId, "in", false, 0),
                new WorkflowEdge(producerId, "Completed", reviewerId, "in", false, 0),
            },
            Inputs: Array.Empty<WorkflowInput>());
    }

    private static Workflow BuildWorkflowWithLogicNode()
    {
        var startId = Guid.Parse("cccccccc-1111-cccc-cccc-cccccccccccc");
        var logicId = Guid.Parse("cccccccc-2222-cccc-cccc-cccccccccccc");
        var leftId = Guid.Parse("cccccccc-3333-cccc-cccc-cccccccccccc");
        var rightId = Guid.Parse("cccccccc-4444-cccc-cccc-cccccccccccc");

        const string script = """
            if (input && input.path === 'left') {
                setNodePath('Left');
            } else {
                setNodePath('Right');
            }
            """;

        return new Workflow(
            Key: "logic-flow",
            Version: 1,
            Name: "Logic-routed flow",
            MaxRoundsPerRound: 64,
            CreatedAtUtc: DateTime.UtcNow,
            Nodes: new[]
            {
                new WorkflowNode(
                    Id: startId, Kind: WorkflowNodeKind.Start, AgentKey: null, AgentVersion: null,
                    OutputScript: null, OutputPorts: new[] { "Completed" }, LayoutX: 0, LayoutY: 0),
                new WorkflowNode(
                    Id: logicId, Kind: WorkflowNodeKind.Logic, AgentKey: null, AgentVersion: null,
                    OutputScript: script, OutputPorts: new[] { "Left", "Right" }, LayoutX: 100, LayoutY: 0),
                new WorkflowNode(
                    Id: leftId, Kind: WorkflowNodeKind.Agent, AgentKey: "left-agent", AgentVersion: 1,
                    OutputScript: null, OutputPorts: new[] { "LeftCompleted" }, LayoutX: 200, LayoutY: 0),
                new WorkflowNode(
                    Id: rightId, Kind: WorkflowNodeKind.Agent, AgentKey: "right-agent", AgentVersion: 1,
                    OutputScript: null, OutputPorts: new[] { "RightCompleted" }, LayoutX: 200, LayoutY: 100),
            },
            Edges: new[]
            {
                new WorkflowEdge(startId, "Completed", logicId, "in", false, 0),
                new WorkflowEdge(logicId, "Left", leftId, "in", false, 0),
                new WorkflowEdge(logicId, "Right", rightId, "in", false, 0),
            },
            Inputs: Array.Empty<WorkflowInput>());
    }

    private sealed class MultiWorkflowFakeRepository : IWorkflowRepository
    {
        private readonly Dictionary<string, Workflow> byKey;

        public MultiWorkflowFakeRepository(params Workflow[] workflows)
        {
            byKey = workflows.ToDictionary(w => w.Key, StringComparer.Ordinal);

            // Seed a "left-agent" / "right-agent" / "producer" / "reviewer" -- they're stubs the
            // executor never invokes since the agent invoker never runs in dry-run; we just need
            // the workflow + agent maps to exist, which is the responsibility of the executor's
            // own mocks dictionary.
        }

        public Task<Workflow> GetAsync(string key, int version, CancellationToken cancellationToken = default) =>
            byKey.TryGetValue(key, out var workflow)
                ? Task.FromResult(workflow)
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
