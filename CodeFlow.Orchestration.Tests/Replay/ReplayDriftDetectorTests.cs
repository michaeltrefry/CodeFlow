using CodeFlow.Orchestration.Replay;
using CodeFlow.Persistence;
using FluentAssertions;

namespace CodeFlow.Orchestration.Tests.Replay;

public sealed class ReplayDriftDetectorTests
{
    private static readonly Guid StartId = Guid.Parse("aaaaaaaa-1111-1111-1111-aaaaaaaaaaaa");
    private static readonly Guid AgentId = Guid.Parse("aaaaaaaa-2222-2222-2222-aaaaaaaaaaaa");

    [Fact]
    public void IdenticalWorkflow_NoOverride_ReturnsNone()
    {
        var wf = BuildSimpleWorkflow(version: 1);
        var pinned = new Dictionary<string, int> { ["worker"] = 1 };

        var report = ReplayDriftDetector.Detect(
            wf, pinned, wf,
            new[] { Decision("worker", AgentId) });

        report.Level.Should().Be(DriftLevel.None);
        report.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void DeletedReferencedNode_ReturnsHardDrift()
    {
        var original = BuildSimpleWorkflow(version: 1);
        var target = original with { Nodes = original.Nodes.Where(n => n.Id != AgentId).ToArray() };

        var report = ReplayDriftDetector.Detect(
            original,
            new Dictionary<string, int> { ["worker"] = 1 },
            target,
            new[] { Decision("worker", AgentId) });

        report.Level.Should().Be(DriftLevel.Hard);
        report.Warnings.Should().Contain(w => w.Contains(AgentId.ToString()) && w.Contains("not present"));
    }

    [Fact]
    public void NodeKindChanged_ReturnsHardDrift()
    {
        var original = BuildSimpleWorkflow(version: 1);
        var target = original with
        {
            Version = 2,
            Nodes = original.Nodes.Select(n =>
                n.Id == AgentId ? n with { Kind = WorkflowNodeKind.Logic, AgentKey = null } : n).ToArray()
        };

        var report = ReplayDriftDetector.Detect(
            original,
            new Dictionary<string, int> { ["worker"] = 1 },
            target,
            new[] { Decision("worker", AgentId) });

        report.Level.Should().Be(DriftLevel.Hard);
        report.Warnings.Should().Contain(w => w.Contains("kind"));
    }

    [Fact]
    public void AgentVersionMoved_ReturnsSoftDrift()
    {
        var original = BuildSimpleWorkflow(version: 1);
        var target = original with
        {
            Version = 2,
            Nodes = original.Nodes.Select(n =>
                n.Id == AgentId ? n with { AgentVersion = 2 } : n).ToArray()
        };

        var report = ReplayDriftDetector.Detect(
            original,
            new Dictionary<string, int> { ["worker"] = 1 },
            target,
            new[] { Decision("worker", AgentId) });

        report.Level.Should().Be(DriftLevel.Soft);
        report.Warnings.Should().Contain(w => w.Contains("worker") && w.Contains("1") && w.Contains("2"));
    }

    [Fact]
    public void StructurallyEquivalent_AcrossLayoutChanges_ReturnsTrue()
    {
        var a = BuildSimpleWorkflow(version: 1);
        var b = a with
        {
            Version = 2,
            Nodes = a.Nodes.Select(n => n with { LayoutX = n.LayoutX + 100, LayoutY = n.LayoutY + 50 }).ToArray()
        };

        var equivalent = ReplayDriftDetector.StructurallyEquivalent(a, b, out var diffs);

        equivalent.Should().BeTrue();
        diffs.Should().BeEmpty();
    }

    [Fact]
    public void StructurallyEquivalent_NewEdge_ReturnsFalseWithDiff()
    {
        var a = BuildSimpleWorkflow(version: 1);
        var newNodeId = Guid.Parse("bbbbbbbb-1111-1111-1111-bbbbbbbbbbbb");
        var b = a with
        {
            Version = 2,
            Nodes = a.Nodes.Concat(new[]
            {
                new WorkflowNode(
                    Id: newNodeId, Kind: WorkflowNodeKind.Agent, AgentKey: "extra", AgentVersion: 1,
                    OutputScript: null, OutputPorts: new[] { "Completed" }, LayoutX: 300, LayoutY: 0),
            }).ToArray(),
            Edges = a.Edges.Concat(new[]
            {
                new WorkflowEdge(AgentId, "Completed", newNodeId, "in", false, 1),
            }).ToArray()
        };

        var equivalent = ReplayDriftDetector.StructurallyEquivalent(a, b, out var diffs);

        equivalent.Should().BeFalse();
        diffs.Should().NotBeEmpty();
    }

    [Fact]
    public void EditingBoundaryScripts_OnSubflowOrReviewLoop_DoesNotTriggerDrift()
    {
        // sc-628: ReplayDriftDetector intentionally ignores on-node script text (see class
        // doc-comment) so cosmetic script edits don't inflate Soft drift on every save. The
        // boundary input/output script slots on Subflow and ReviewLoop nodes inherit that
        // behavior — editing them must not surface drift even though they materially affect
        // the dry-run trajectory. Replay reruns rebuild from the target workflow's scripts; the
        // saga's recorded mocks pin agent submissions, not script choices, so the rerun stays
        // safe under boundary-script edits.
        var subflowId = Guid.Parse("cccccccc-1111-1111-1111-cccccccccccc");
        var loopId = Guid.Parse("cccccccc-2222-2222-2222-cccccccccccc");
        var subflowChildId = Guid.Parse("cccccccc-3333-3333-3333-cccccccccccc");
        var loopChildId = Guid.Parse("cccccccc-4444-4444-4444-cccccccccccc");

        var original = new Workflow(
            Key: "drift-boundary-scripts",
            Version: 1,
            Name: "drift-boundary",
            MaxRoundsPerRound: 64,
            CreatedAtUtc: DateTime.UtcNow,
            Nodes: new[]
            {
                new WorkflowNode(
                    Id: StartId, Kind: WorkflowNodeKind.Start, AgentKey: "kickoff", AgentVersion: 1,
                    OutputScript: null, OutputPorts: new[] { "Completed" }, LayoutX: 0, LayoutY: 0),
                new WorkflowNode(
                    Id: subflowId, Kind: WorkflowNodeKind.Subflow, AgentKey: null, AgentVersion: null,
                    OutputScript: "setWorkflow('a', 'v1');",
                    OutputPorts: new[] { "Completed" }, LayoutX: 100, LayoutY: 0,
                    SubflowKey: "child-a", SubflowVersion: 1,
                    InputScript: "setWorkflow('b', 'v1');"),
                new WorkflowNode(
                    Id: loopId, Kind: WorkflowNodeKind.ReviewLoop, AgentKey: null, AgentVersion: null,
                    OutputScript: "setWorkflow('c', 'v1');",
                    OutputPorts: new[] { "Approved" }, LayoutX: 200, LayoutY: 0,
                    SubflowKey: "child-b", SubflowVersion: 1,
                    InputScript: "setWorkflow('d', 'v1');",
                    ReviewMaxRounds: 3, LoopDecision: "Rejected"),
            },
            Edges: new[]
            {
                new WorkflowEdge(StartId, "Completed", subflowId, "in", false, 0),
                new WorkflowEdge(subflowId, "Completed", loopId, "in", false, 0),
            },
            Inputs: Array.Empty<WorkflowInput>());

        // Same workflow with both InputScript and OutputScript replaced on the boundary nodes.
        // Structurally identical: same node ids, kinds, agent pins, and edge topology.
        var target = original with
        {
            Version = 2,
            Nodes = original.Nodes.Select(n => n.Id switch
            {
                _ when n.Id == subflowId => n with
                {
                    InputScript = "setWorkflow('b', 'v2-changed'); /* edited */",
                    OutputScript = "setWorkflow('a', 'v2-changed'); /* edited */",
                },
                _ when n.Id == loopId => n with
                {
                    InputScript = "setWorkflow('d', 'v2-changed'); /* edited */",
                    OutputScript = "setWorkflow('c', 'v2-changed'); /* edited */",
                },
                _ => n,
            }).ToArray(),
        };

        var report = ReplayDriftDetector.Detect(
            original,
            // No pins to track — the boundary nodes have no AgentKey and the Start node isn't
            // in the agent-version pin map (the detector tracks Agent/Hitl kinds only).
            new Dictionary<string, int>(),
            target,
            new[]
            {
                // Boundary-node decisions are recorded with synthesized agent keys (subflow:* /
                // review-loop:*); mirror that here so the detector sees the node refs and runs
                // its kind/agent-key check against the target nodes.
                Decision("subflow:child-a", subflowId),
                Decision("review-loop:child-b", loopId),
            });

        report.Level.Should().Be(DriftLevel.None);
        report.Warnings.Should().BeEmpty(
            because: "the detector deliberately ignores on-node script text — boundary scripts " +
                     "follow the same convention as Agent/Hitl/Start scripts.");
    }

    private static RecordedDecisionRef Decision(string agent, Guid nodeId) =>
        new(agent, OrdinalPerAgent: 1, SagaCorrelationId: Guid.NewGuid(), SagaOrdinal: 0,
            NodeId: nodeId, RoundId: Guid.NewGuid(), OriginalDecision: "Completed");

    private static Workflow BuildSimpleWorkflow(int version) =>
        new(
            Key: "drift-test",
            Version: version,
            Name: "drift",
            MaxRoundsPerRound: 64,
            CreatedAtUtc: DateTime.UtcNow,
            Nodes: new[]
            {
                new WorkflowNode(
                    Id: StartId, Kind: WorkflowNodeKind.Start, AgentKey: null, AgentVersion: null,
                    OutputScript: null, OutputPorts: new[] { "Completed" }, LayoutX: 0, LayoutY: 0),
                new WorkflowNode(
                    Id: AgentId, Kind: WorkflowNodeKind.Agent, AgentKey: "worker", AgentVersion: 1,
                    OutputScript: null, OutputPorts: new[] { "Completed", "Failed" }, LayoutX: 100, LayoutY: 0),
            },
            Edges: new[]
            {
                new WorkflowEdge(StartId, "Completed", AgentId, "in", false, 0),
            },
            Inputs: Array.Empty<WorkflowInput>());
}
