using CodeFlow.Orchestration;
using CodeFlow.Orchestration.Scripting;
using CodeFlow.Persistence;
using FluentAssertions;

namespace CodeFlow.Orchestration.Tests.Scripting;

/// <summary>
/// F2 analyzer integration tests. Builds workflows in-memory (no DB) and asserts the
/// per-node scope reflects upstream script writes, mirror+rejection-history features, and
/// graph-shape input source resolution.
/// </summary>
public sealed class WorkflowDataflowAnalyzerTests
{
    [Fact]
    public void Analyze_StartNodeOnly_WorkflowVarsEmpty_NoInputSource()
    {
        var startId = Guid.NewGuid();
        var workflow = BuildWorkflow(
            "wf-start-only",
            new[]
            {
                Node(startId, WorkflowNodeKind.Start, "kickoff", outputScript: null),
            },
            Array.Empty<WorkflowEdge>());

        var analyzer = new WorkflowDataflowAnalyzer();
        var snapshot = analyzer.Analyze(workflow);

        snapshot.ScopesByNode.Should().ContainKey(startId);
        var scope = snapshot.ScopesByNode[startId];
        scope.WorkflowVariables.Should().BeEmpty();
        scope.ContextKeys.Should().BeEmpty();
        scope.InputSource.Should().BeNull();
    }

    [Fact]
    public void Analyze_DownstreamNodeSeesUpstreamSetWorkflowAsDefinite()
    {
        // Architect writes `currentPlan` via setWorkflow at top level → reviewer (downstream)
        // sees it as definite.
        var architectId = Guid.NewGuid();
        var reviewerId = Guid.NewGuid();
        var workflow = BuildWorkflow(
            "wf-arch-reviewer",
            new[]
            {
                Node(architectId, WorkflowNodeKind.Start, "architect",
                    outputScript: "setWorkflow('currentPlan', input.text);"),
                Node(reviewerId, WorkflowNodeKind.Agent, "reviewer", outputScript: null),
            },
            new[]
            {
                Edge(architectId, "Continue", reviewerId, "in", sortOrder: 0),
            });

        var analyzer = new WorkflowDataflowAnalyzer();
        var scope = analyzer.GetScope(workflow, reviewerId)!;

        scope.WorkflowVariables.Should().ContainSingle()
            .Which.Should().Match<DataflowVariable>(v =>
                v.Key == "currentPlan" && v.Confidence == DataflowConfidence.Definite);
        scope.WorkflowVariables[0].Sources.Should().ContainSingle()
            .Which.NodeId.Should().Be(architectId);
        scope.InputSource.Should().NotBeNull();
        scope.InputSource!.NodeId.Should().Be(architectId);
        scope.InputSource.Port.Should().Be("Continue");
    }

    [Fact]
    public void Analyze_UpstreamConditionalWrite_IsConditionalForDownstream()
    {
        var producerId = Guid.NewGuid();
        var consumerId = Guid.NewGuid();
        var workflow = BuildWorkflow(
            "wf-conditional",
            new[]
            {
                Node(producerId, WorkflowNodeKind.Start, "producer",
                    outputScript: """
                        if (input.flag) {
                            setWorkflow('maybeKey', input.value);
                        }
                        """),
                Node(consumerId, WorkflowNodeKind.Agent, "consumer", outputScript: null),
            },
            new[]
            {
                Edge(producerId, "Continue", consumerId, "in", sortOrder: 0),
            });

        var analyzer = new WorkflowDataflowAnalyzer();
        var scope = analyzer.GetScope(workflow, consumerId)!;

        scope.WorkflowVariables.Should().ContainSingle()
            .Which.Should().Match<DataflowVariable>(v =>
                v.Key == "maybeKey" && v.Confidence == DataflowConfidence.Conditional);
    }

    [Fact]
    public void Analyze_VariableWrittenDefinitelyAndConditionally_AggregatesAsDefinite()
    {
        // One ancestor writes 'x' definitely; another writes 'x' conditionally. Final
        // confidence is Definite — at least one path always writes the value.
        var nodeA = Guid.NewGuid();
        var nodeB = Guid.NewGuid();
        var sink = Guid.NewGuid();
        var workflow = BuildWorkflow(
            "wf-mixed",
            new[]
            {
                Node(nodeA, WorkflowNodeKind.Start, "a",
                    outputScript: "setWorkflow('x', input.va);"),
                Node(nodeB, WorkflowNodeKind.Agent, "b",
                    outputScript: "if (input.flag) { setWorkflow('x', input.vb); }"),
                Node(sink, WorkflowNodeKind.Agent, "sink", outputScript: null),
            },
            new[]
            {
                Edge(nodeA, "Continue", nodeB, "in", sortOrder: 0),
                Edge(nodeB, "Continue", sink, "in", sortOrder: 0),
            });

        var analyzer = new WorkflowDataflowAnalyzer();
        var scope = analyzer.GetScope(workflow, sink)!;

        scope.WorkflowVariables.Should().ContainSingle()
            .Which.Should().Match<DataflowVariable>(v =>
                v.Key == "x" && v.Confidence == DataflowConfidence.Definite);
        scope.WorkflowVariables[0].Sources.Select(s => s.NodeId)
            .Should().BeEquivalentTo(new[] { nodeA, nodeB });
    }

    [Fact]
    public void Analyze_MirrorOutputToWorkflowVar_TreatedAsDefiniteWrite()
    {
        // P4 integration: a node configured with MirrorOutputToWorkflowVar='currentPlan'
        // writes that key BEFORE its output script runs. Downstream nodes see it as definite
        // even when the node has no output script.
        var producerId = Guid.NewGuid();
        var consumerId = Guid.NewGuid();
        var workflow = BuildWorkflow(
            "wf-mirror",
            new[]
            {
                NodeWith(producerId, WorkflowNodeKind.Start, "producer",
                    outputScript: null,
                    mirrorOutputToWorkflowVar: "currentPlan"),
                Node(consumerId, WorkflowNodeKind.Agent, "consumer", outputScript: null),
            },
            new[]
            {
                Edge(producerId, "Continue", consumerId, "in", sortOrder: 0),
            });

        var analyzer = new WorkflowDataflowAnalyzer();
        var scope = analyzer.GetScope(workflow, consumerId)!;

        scope.WorkflowVariables.Should().ContainSingle()
            .Which.Should().Match<DataflowVariable>(v =>
                v.Key == "currentPlan" && v.Confidence == DataflowConfidence.Definite);
    }

    [Fact]
    public void Analyze_ReviewLoopWithRejectionHistory_TreatedAsDefiniteSourceForLoopVar()
    {
        // P3 integration: a ReviewLoop with rejection-history enabled writes the framework
        // variable __loop.rejectionHistory. Downstream-of-the-loop tooling can rely on it.
        var startId = Guid.NewGuid();
        var loopId = Guid.NewGuid();
        var sinkId = Guid.NewGuid();
        var workflow = BuildWorkflow(
            "wf-rh",
            new[]
            {
                Node(startId, WorkflowNodeKind.Start, "trigger", outputScript: null),
                NodeWith(loopId, WorkflowNodeKind.ReviewLoop, agentKey: null,
                    outputScript: null,
                    subflowKey: "inner",
                    subflowVersion: 1,
                    reviewMaxRounds: 3,
                    rejectionHistory: new RejectionHistoryConfig(Enabled: true)),
                Node(sinkId, WorkflowNodeKind.Agent, "sink", outputScript: null),
            },
            new[]
            {
                Edge(startId, "Continue", loopId, "in", sortOrder: 0),
                Edge(loopId, "Approved", sinkId, "in", sortOrder: 0),
            });

        var analyzer = new WorkflowDataflowAnalyzer();
        var scope = analyzer.GetScope(workflow, sinkId)!;

        scope.WorkflowVariables.Should().Contain(v =>
            v.Key == RejectionHistoryAccumulator.WorkflowVariableKey
            && v.Confidence == DataflowConfidence.Definite);
    }

    [Fact]
    public void Analyze_ReviewLoopNode_LoopBindingsExposeMaxRounds()
    {
        var startId = Guid.NewGuid();
        var loopId = Guid.NewGuid();
        var workflow = BuildWorkflow(
            "wf-loop-bindings",
            new[]
            {
                Node(startId, WorkflowNodeKind.Start, "trigger", outputScript: null),
                NodeWith(loopId, WorkflowNodeKind.ReviewLoop, agentKey: null,
                    outputScript: null,
                    subflowKey: "inner",
                    subflowVersion: 1,
                    reviewMaxRounds: 7),
            },
            new[]
            {
                Edge(startId, "Continue", loopId, "in", sortOrder: 0),
            });

        var analyzer = new WorkflowDataflowAnalyzer();
        var scope = analyzer.GetScope(workflow, loopId)!;

        scope.LoopBindings.Should().NotBeNull();
        scope.LoopBindings!.MaxRounds.Should().Be(7);
        scope.LoopBindings.StaticRound.Should().BeNull();
    }

    [Fact]
    public void Analyze_ResultsCachedAcrossCallsForSameVersion()
    {
        // Sanity: two Analyze calls for the same workflow object return the SAME snapshot
        // instance — proves the cache key path works.
        var startId = Guid.NewGuid();
        var workflow = BuildWorkflow(
            "wf-cache",
            new[] { Node(startId, WorkflowNodeKind.Start, "kickoff", outputScript: null) },
            Array.Empty<WorkflowEdge>());

        var analyzer = new WorkflowDataflowAnalyzer();
        var first = analyzer.Analyze(workflow);
        var second = analyzer.Analyze(workflow);

        second.Should().BeSameAs(first);
    }

    [Fact]
    public void Analyze_ScriptParseError_SurfacesAsDiagnosticNotException()
    {
        var startId = Guid.NewGuid();
        var workflow = BuildWorkflow(
            "wf-bad-script",
            new[]
            {
                Node(startId, WorkflowNodeKind.Start, "kickoff",
                    outputScript: "this is not valid javascript at all"),
            },
            Array.Empty<WorkflowEdge>());

        var analyzer = new WorkflowDataflowAnalyzer();
        var snapshot = analyzer.Analyze(workflow);

        snapshot.Diagnostics.Should().NotBeEmpty();
        snapshot.Diagnostics.Should().AllSatisfy(d =>
            d.NodeId.Should().Be(startId));
    }

    [Fact]
    public void Analyze_FanInGraph_AggregatesWritesFromBothBranches()
    {
        // Diamond-ish shape: kickoff → branchA + branchB → join. Join sees both keys.
        var startId = Guid.NewGuid();
        var branchAId = Guid.NewGuid();
        var branchBId = Guid.NewGuid();
        var joinId = Guid.NewGuid();
        var workflow = BuildWorkflow(
            "wf-diamond",
            new[]
            {
                Node(startId, WorkflowNodeKind.Start, "kickoff", outputScript: null),
                Node(branchAId, WorkflowNodeKind.Agent, "a",
                    outputScript: "setWorkflow('keyA', input.va);"),
                Node(branchBId, WorkflowNodeKind.Agent, "b",
                    outputScript: "setWorkflow('keyB', input.vb);"),
                Node(joinId, WorkflowNodeKind.Agent, "join", outputScript: null),
            },
            new[]
            {
                Edge(startId, "Continue", branchAId, "in", sortOrder: 0),
                Edge(startId, "Continue", branchBId, "in", sortOrder: 1),
                Edge(branchAId, "Continue", joinId, "in", sortOrder: 0),
                Edge(branchBId, "Continue", joinId, "in", sortOrder: 1),
            });

        var analyzer = new WorkflowDataflowAnalyzer();
        var scope = analyzer.GetScope(workflow, joinId)!;

        scope.WorkflowVariables.Select(v => v.Key)
            .Should().BeEquivalentTo(new[] { "keyA", "keyB" });
    }

    [Fact]
    public void Analyze_SubflowBoundaryScripts_TreatedAsDefiniteSourcesForDownstream()
    {
        // sc-628: a Subflow node carries its own input + output scripts; both run in the parent
        // saga's scope (input before the child is dispatched, output after the child terminates).
        // setWorkflow writes from either script must surface as definite sources to nodes that
        // sit downstream of the boundary.
        var startId = Guid.NewGuid();
        var subflowId = Guid.NewGuid();
        var sinkId = Guid.NewGuid();
        var workflow = BuildWorkflow(
            "wf-subflow-boundary",
            new[]
            {
                Node(startId, WorkflowNodeKind.Start, "kickoff", outputScript: null),
                NodeWith(subflowId, WorkflowNodeKind.Subflow, agentKey: null,
                    outputScript: "setWorkflow('boundaryOutVar', 'set-on-exit');",
                    inputScript: "setWorkflow('boundaryInVar', 'set-on-entry');",
                    subflowKey: "child", subflowVersion: 1),
                Node(sinkId, WorkflowNodeKind.Agent, "sink", outputScript: null),
            },
            new[]
            {
                Edge(startId, "Continue", subflowId, "in", sortOrder: 0),
                Edge(subflowId, "Continue", sinkId, "in", sortOrder: 0),
            });

        var analyzer = new WorkflowDataflowAnalyzer();
        var scope = analyzer.GetScope(workflow, sinkId)!;

        scope.WorkflowVariables.Select(v => v.Key)
            .Should().Contain(new[] { "boundaryInVar", "boundaryOutVar" });
        var inVar = scope.WorkflowVariables.Single(v => v.Key == "boundaryInVar");
        inVar.Confidence.Should().Be(DataflowConfidence.Definite);
        inVar.Sources.Should().ContainSingle().Which.NodeId.Should().Be(subflowId);
        var outVar = scope.WorkflowVariables.Single(v => v.Key == "boundaryOutVar");
        outVar.Confidence.Should().Be(DataflowConfidence.Definite);
        outVar.Sources.Should().ContainSingle().Which.NodeId.Should().Be(subflowId);
    }

    [Fact]
    public void Analyze_ReviewLoopBoundaryScripts_TreatedAsDefiniteSourcesForDownstream()
    {
        // sc-628 ReviewLoop variant: boundary input script runs once before round 1, output
        // script runs once after the loop terminates. Both write workflow keys visible to
        // downstream parent nodes.
        var startId = Guid.NewGuid();
        var loopId = Guid.NewGuid();
        var sinkId = Guid.NewGuid();
        var workflow = BuildWorkflow(
            "wf-reviewloop-boundary",
            new[]
            {
                Node(startId, WorkflowNodeKind.Start, "kickoff", outputScript: null),
                NodeWith(loopId, WorkflowNodeKind.ReviewLoop, agentKey: null,
                    outputScript: "setWorkflow('loopOutVar', 'set-on-exit');",
                    inputScript: "setWorkflow('loopInVar', 'set-on-entry');",
                    subflowKey: "loop-child", subflowVersion: 1, reviewMaxRounds: 3),
                Node(sinkId, WorkflowNodeKind.Agent, "sink", outputScript: null),
            },
            new[]
            {
                Edge(startId, "Continue", loopId, "in", sortOrder: 0),
                Edge(loopId, "Approved", sinkId, "in", sortOrder: 0),
            });

        var analyzer = new WorkflowDataflowAnalyzer();
        var scope = analyzer.GetScope(workflow, sinkId)!;

        scope.WorkflowVariables.Select(v => v.Key)
            .Should().Contain(new[] { "loopInVar", "loopOutVar" });
        scope.WorkflowVariables.Single(v => v.Key == "loopInVar").Confidence
            .Should().Be(DataflowConfidence.Definite);
        scope.WorkflowVariables.Single(v => v.Key == "loopOutVar").Confidence
            .Should().Be(DataflowConfidence.Definite);
    }

    private static WorkflowNode Node(
        Guid id,
        WorkflowNodeKind kind,
        string? agentKey,
        string? outputScript) => new(
            Id: id,
            Kind: kind,
            AgentKey: agentKey,
            AgentVersion: agentKey is null ? null : 1,
            OutputScript: outputScript,
            OutputPorts: new[] { "Continue", "Failed" },
            LayoutX: 0, LayoutY: 0);

    private static WorkflowNode NodeWith(
        Guid id,
        WorkflowNodeKind kind,
        string? agentKey,
        string? outputScript,
        string? mirrorOutputToWorkflowVar = null,
        string? subflowKey = null,
        int? subflowVersion = null,
        int? reviewMaxRounds = null,
        RejectionHistoryConfig? rejectionHistory = null,
        string? inputScript = null) => new(
            Id: id,
            Kind: kind,
            AgentKey: agentKey,
            AgentVersion: agentKey is null ? null : 1,
            OutputScript: outputScript,
            OutputPorts: new[] { "Continue", "Approved", "Exhausted", "Failed" },
            LayoutX: 0, LayoutY: 0,
            SubflowKey: subflowKey,
            SubflowVersion: subflowVersion,
            ReviewMaxRounds: reviewMaxRounds,
            RejectionHistory: rejectionHistory,
            MirrorOutputToWorkflowVar: mirrorOutputToWorkflowVar,
            InputScript: inputScript);

    private static WorkflowEdge Edge(Guid from, string fromPort, Guid to, string toPort, int sortOrder) =>
        new(from, fromPort, to, toPort, RotatesRound: false, SortOrder: sortOrder);

    private static Workflow BuildWorkflow(
        string key,
        IReadOnlyList<WorkflowNode> nodes,
        IReadOnlyList<WorkflowEdge> edges) => new(
            Key: key,
            Version: 1,
            Name: key,
            MaxRoundsPerRound: 3,
            CreatedAtUtc: DateTime.UtcNow,
            Nodes: nodes,
            Edges: edges,
            Inputs: Array.Empty<WorkflowInput>());
}
