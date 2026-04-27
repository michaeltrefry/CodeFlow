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

    /// <summary>
    /// V2 input scripts run on Start nodes — they can seed workflow variables and override the
    /// input artifact via setInput. Mirrors the saga's TryEvaluateInputScriptAsync.
    /// </summary>
    [Fact]
    public async Task InputScript_OnStart_SeedsWorkflowVarsAndOverridesInput()
    {
        var startId = Guid.Parse("11ee1111-1111-1111-1111-1111111111ee");
        var agentId = Guid.Parse("22ee2222-2222-2222-2222-2222222222ee");
        var workflow = new Workflow(
            Key: "input-script-start",
            Version: 1,
            Name: "Input script on Start",
            MaxRoundsPerRound: 64,
            CreatedAtUtc: DateTime.UtcNow,
            Nodes: new[]
            {
                new WorkflowNode(
                    Id: startId, Kind: WorkflowNodeKind.Start, AgentKey: null, AgentVersion: null,
                    OutputScript: null, OutputPorts: new[] { "Continue" }, LayoutX: 0, LayoutY: 0,
                    InputScript: "setWorkflow('seeded', 'yes'); setInput('overridden');"),
                new WorkflowNode(
                    Id: agentId, Kind: WorkflowNodeKind.Agent, AgentKey: "echo", AgentVersion: 1,
                    OutputScript: null, OutputPorts: new[] { "Done" }, LayoutX: 100, LayoutY: 0),
            },
            Edges: new[]
            {
                new WorkflowEdge(startId, "Continue", agentId, "in", false, 0),
            },
            Inputs: Array.Empty<WorkflowInput>());

        var repo = new MultiWorkflowFakeRepository(workflow);
        var executor = new DryRunExecutor(repo, new LogicNodeScriptHost(new MemoryCache(new MemoryCacheOptions())));

        var mocks = new Dictionary<string, IReadOnlyList<DryRunMockResponse>>
        {
            ["echo"] = new[] { new DryRunMockResponse("Done", "agent saw the input", null) },
        };

        var result = await executor.ExecuteAsync(
            new DryRunRequest("input-script-start", null, "original input", mocks),
            CancellationToken.None);

        result.State.Should().Be(DryRunTerminalState.Completed);
        result.WorkflowVariables.Should().ContainKey("seeded");
        result.WorkflowVariables["seeded"].GetString().Should().Be("yes");
        // The script's setInput overrides the artifact passed downstream — the agent receives
        // 'overridden' rather than 'original input'.
        result.Events.Should().Contain(e =>
            e.Kind == DryRunEventKind.NodeEntered && e.NodeId == agentId && e.InputPreview == "overridden");
    }

    /// <summary>
    /// V2 input scripts run on Agent nodes BEFORE the mock is dequeued. Variables seeded by the
    /// input script are visible to anything downstream — including the same node's output script.
    /// </summary>
    [Fact]
    public async Task InputScript_OnAgent_RunsBeforeMockConsumed()
    {
        var startId = Guid.Parse("33ee3333-3333-3333-3333-3333333333ee");
        var agentId = Guid.Parse("44ee4444-4444-4444-4444-4444444444ee");
        var workflow = new Workflow(
            Key: "input-script-agent",
            Version: 1,
            Name: "Input script on Agent",
            MaxRoundsPerRound: 64,
            CreatedAtUtc: DateTime.UtcNow,
            Nodes: new[]
            {
                new WorkflowNode(
                    Id: startId, Kind: WorkflowNodeKind.Start, AgentKey: null, AgentVersion: null,
                    OutputScript: null, OutputPorts: new[] { "Continue" }, LayoutX: 0, LayoutY: 0),
                new WorkflowNode(
                    Id: agentId, Kind: WorkflowNodeKind.Agent, AgentKey: "agent", AgentVersion: 1,
                    OutputScript: null, OutputPorts: new[] { "Done" }, LayoutX: 100, LayoutY: 0,
                    InputScript: "setWorkflow('preMock', 'set-by-input-script');"),
            },
            Edges: new[]
            {
                new WorkflowEdge(startId, "Continue", agentId, "in", false, 0),
            },
            Inputs: Array.Empty<WorkflowInput>());

        var repo = new MultiWorkflowFakeRepository(workflow);
        var executor = new DryRunExecutor(repo, new LogicNodeScriptHost(new MemoryCache(new MemoryCacheOptions())));

        var mocks = new Dictionary<string, IReadOnlyList<DryRunMockResponse>>
        {
            ["agent"] = new[] { new DryRunMockResponse("Done", "agent done", null) },
        };

        var result = await executor.ExecuteAsync(
            new DryRunRequest("input-script-agent", null, "x", mocks),
            CancellationToken.None);

        result.State.Should().Be(DryRunTerminalState.Completed);
        result.WorkflowVariables.Should().ContainKey("preMock");
        // The input-script LogicEvaluated event must precede the AgentMockApplied event for the
        // same node.
        var inputScriptOrdinal = result.Events
            .First(e => e.Kind == DryRunEventKind.LogicEvaluated && e.NodeId == agentId)
            .Ordinal;
        var mockAppliedOrdinal = result.Events
            .First(e => e.Kind == DryRunEventKind.AgentMockApplied && e.NodeId == agentId)
            .Ordinal;
        inputScriptOrdinal.Should().BeLessThan(mockAppliedOrdinal);
    }

    /// <summary>
    /// V2 output scripts on Agent nodes can override the routing port via setNodePath and the
    /// artifact text via setOutput, with setWorkflow side-effects applied. Mirrors the saga's
    /// ResolveSourcePortAsync output-script branch.
    /// </summary>
    [Fact]
    public async Task OutputScript_OnAgent_OverridesPortAndArtifact()
    {
        var startId = Guid.Parse("55ee5555-5555-5555-5555-5555555555ee");
        var agentId = Guid.Parse("66ee6666-6666-6666-6666-6666666666ee");
        var leftId = Guid.Parse("77ee7777-7777-7777-7777-7777777777ee");
        var rightId = Guid.Parse("88ee8888-8888-8888-8888-8888888888ee");

        const string outputScript = """
            setWorkflow('seenDecision', output.decision);
            // Force routing to Right regardless of the agent's decision.
            setNodePath('Right');
            setOutput('artifact-from-script');
            """;

        var workflow = new Workflow(
            Key: "output-script-agent",
            Version: 1,
            Name: "Output script on Agent",
            MaxRoundsPerRound: 64,
            CreatedAtUtc: DateTime.UtcNow,
            Nodes: new[]
            {
                new WorkflowNode(
                    Id: startId, Kind: WorkflowNodeKind.Start, AgentKey: null, AgentVersion: null,
                    OutputScript: null, OutputPorts: new[] { "Continue" }, LayoutX: 0, LayoutY: 0),
                new WorkflowNode(
                    Id: agentId, Kind: WorkflowNodeKind.Agent, AgentKey: "router", AgentVersion: 1,
                    OutputScript: outputScript, OutputPorts: new[] { "Left", "Right" }, LayoutX: 100, LayoutY: 0),
                new WorkflowNode(
                    Id: leftId, Kind: WorkflowNodeKind.Agent, AgentKey: "left-agent", AgentVersion: 1,
                    OutputScript: null, OutputPorts: new[] { "LeftDone" }, LayoutX: 200, LayoutY: 0),
                new WorkflowNode(
                    Id: rightId, Kind: WorkflowNodeKind.Agent, AgentKey: "right-agent", AgentVersion: 1,
                    OutputScript: null, OutputPorts: new[] { "RightDone" }, LayoutX: 200, LayoutY: 100),
            },
            Edges: new[]
            {
                new WorkflowEdge(startId, "Continue", agentId, "in", false, 0),
                new WorkflowEdge(agentId, "Left", leftId, "in", false, 0),
                new WorkflowEdge(agentId, "Right", rightId, "in", false, 0),
            },
            Inputs: Array.Empty<WorkflowInput>());

        var repo = new MultiWorkflowFakeRepository(workflow);
        var executor = new DryRunExecutor(repo, new LogicNodeScriptHost(new MemoryCache(new MemoryCacheOptions())));

        var mocks = new Dictionary<string, IReadOnlyList<DryRunMockResponse>>
        {
            // Agent says "Left" but the script overrides to "Right".
            ["router"] = new[] { new DryRunMockResponse("Left", "agent body", null) },
            ["right-agent"] = new[] { new DryRunMockResponse("RightDone", "right received it", null) },
        };

        var result = await executor.ExecuteAsync(
            new DryRunRequest("output-script-agent", null, "x", mocks),
            CancellationToken.None);

        result.State.Should().Be(DryRunTerminalState.Completed);
        result.TerminalPort.Should().Be("RightDone");
        result.WorkflowVariables.Should().ContainKey("seenDecision");
        result.WorkflowVariables["seenDecision"].GetString().Should().Be("Left",
            because: "the script reads output.decision (the mock's port) before overriding.");
        // The right-agent must have received the script-overridden artifact, not the agent's
        // original 'agent body'.
        result.Events.Should().Contain(e =>
            e.Kind == DryRunEventKind.NodeEntered && e.NodeId == rightId && e.InputPreview == "artifact-from-script");
    }

    /// <summary>
    /// V2 P3 rejection-history accumulation: when the parent ReviewLoop has rejectionHistory
    /// enabled, each loopDecision-port round writes to the framework-managed
    /// __loop.rejectionHistory variable in the workflow bag.
    /// </summary>
    [Fact]
    public async Task ReviewLoop_RejectionHistory_AccumulatesAcrossRounds()
    {
        var (outer, inner) = BuildReviewLoopPair(maxRounds: 4);
        var loopNode = outer.Nodes.Single(n => n.Id == ReviewLoopId);
        var loopWithHistory = loopNode with
        {
            RejectionHistory = new RejectionHistoryConfig(
                Enabled: true,
                MaxBytes: 32_768,
                Format: RejectionHistoryFormat.Markdown),
        };
        var outerWithHistory = outer with
        {
            Nodes = outer.Nodes.Select(n => n.Id == ReviewLoopId ? loopWithHistory : n).ToArray(),
        };

        var repo = new MultiWorkflowFakeRepository(outerWithHistory, inner);
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
                new DryRunMockResponse("Rejected", "round 1 findings", null),
                new DryRunMockResponse("Rejected", "round 2 findings", null),
                new DryRunMockResponse("Approved", "looks good now", null),
            },
        };

        var result = await executor.ExecuteAsync(
            new DryRunRequest("outer", null, "PRD", mocks),
            CancellationToken.None);

        result.State.Should().Be(DryRunTerminalState.Completed);
        result.TerminalPort.Should().Be("Approved");

        result.WorkflowVariables.Should().ContainKey(RejectionHistoryAccumulator.WorkflowVariableKey);
        var historyText = result.WorkflowVariables[RejectionHistoryAccumulator.WorkflowVariableKey].GetString();
        historyText.Should().Contain("## Round 1\nround 1 findings");
        historyText.Should().Contain("## Round 2\nround 2 findings");
        historyText.Should().NotContain("looks good now",
            because: "the Approved round does not match loopDecision='Rejected' so it is not appended.");

        result.Events.Where(e => e.Kind == DryRunEventKind.BuiltinApplied
                && (e.Message?.StartsWith("P3 rejection-history") ?? false))
            .Should().HaveCount(2);
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
