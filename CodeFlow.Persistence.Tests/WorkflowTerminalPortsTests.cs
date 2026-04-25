using FluentAssertions;

namespace CodeFlow.Persistence.Tests;

public sealed class WorkflowTerminalPortsTests
{
    [Fact]
    public void TerminalPorts_ShouldReturnUnwiredDeclaredPorts()
    {
        var startId = Guid.NewGuid();
        var middleId = Guid.NewGuid();
        var leftId = Guid.NewGuid();
        var rightId = Guid.NewGuid();

        var workflow = new Workflow(
            Key: "fan-out",
            Version: 1,
            Name: "Fan-out",
            MaxRoundsPerRound: 1,
            CreatedAtUtc: DateTime.UtcNow,
            Nodes: new[]
            {
                new WorkflowNode(startId, WorkflowNodeKind.Start, "kickoff", 1, null,
                    new[] { "Completed" }, 0, 0),
                new WorkflowNode(middleId, WorkflowNodeKind.Agent, "router", 1, null,
                    new[] { "Left", "Right" }, 0, 0),
                new WorkflowNode(leftId, WorkflowNodeKind.Agent, "leftPath", 1, null,
                    new[] { "Approved", "Rejected" }, 0, 0),
                new WorkflowNode(rightId, WorkflowNodeKind.Agent, "rightPath", 1, null,
                    new[] { "Done" }, 0, 0),
            },
            Edges: new[]
            {
                new WorkflowEdge(startId, "Completed", middleId, WorkflowEdge.DefaultInputPort, false, 0),
                new WorkflowEdge(middleId, "Left", leftId, WorkflowEdge.DefaultInputPort, false, 1),
                new WorkflowEdge(middleId, "Right", rightId, WorkflowEdge.DefaultInputPort, false, 2),
            },
            Inputs: Array.Empty<WorkflowInput>());

        workflow.TerminalPorts.Should().BeEquivalentTo(new[] { "Approved", "Rejected", "Done" });
    }

    [Fact]
    public void TerminalPorts_ShouldDeduplicateIdenticalPortNamesAcrossNodes()
    {
        var startId = Guid.NewGuid();
        var leftId = Guid.NewGuid();
        var rightId = Guid.NewGuid();

        var workflow = new Workflow(
            Key: "dup",
            Version: 1,
            Name: "Dup",
            MaxRoundsPerRound: 1,
            CreatedAtUtc: DateTime.UtcNow,
            Nodes: new[]
            {
                new WorkflowNode(startId, WorkflowNodeKind.Start, "kickoff", 1, null,
                    new[] { "Left", "Right" }, 0, 0),
                new WorkflowNode(leftId, WorkflowNodeKind.Agent, "a", 1, null,
                    new[] { "Approved" }, 0, 0),
                new WorkflowNode(rightId, WorkflowNodeKind.Agent, "b", 1, null,
                    new[] { "Approved" }, 0, 0),
            },
            Edges: new[]
            {
                new WorkflowEdge(startId, "Left", leftId, WorkflowEdge.DefaultInputPort, false, 0),
                new WorkflowEdge(startId, "Right", rightId, WorkflowEdge.DefaultInputPort, false, 1),
            },
            Inputs: Array.Empty<WorkflowInput>());

        workflow.TerminalPorts.Should().BeEquivalentTo(new[] { "Approved" });
    }

    [Fact]
    public void TerminalPorts_ShouldExcludeImplicitFailedEvenWhenDeclared()
    {
        // Authors should not declare "Failed" — the validator rejects that. But if a legacy
        // workflow round-trips it, TerminalPorts must still drop it: Failed is an error sink,
        // not a designed exit.
        var startId = Guid.NewGuid();
        var workflow = new Workflow(
            Key: "fail",
            Version: 1,
            Name: "Fail",
            MaxRoundsPerRound: 1,
            CreatedAtUtc: DateTime.UtcNow,
            Nodes: new[]
            {
                new WorkflowNode(startId, WorkflowNodeKind.Start, "kickoff", 1, null,
                    new[] { "Completed", "Failed" }, 0, 0),
            },
            Edges: Array.Empty<WorkflowEdge>(),
            Inputs: Array.Empty<WorkflowInput>());

        workflow.TerminalPorts.Should().BeEquivalentTo(new[] { "Completed" });
    }

    [Fact]
    public void TerminalPorts_ShouldIncludeReviewLoopExhaustedWhenUnwired()
    {
        var startId = Guid.NewGuid();
        var loopId = Guid.NewGuid();

        var workflow = new Workflow(
            Key: "rl",
            Version: 1,
            Name: "ReviewLoop",
            MaxRoundsPerRound: 1,
            CreatedAtUtc: DateTime.UtcNow,
            Nodes: new[]
            {
                new WorkflowNode(startId, WorkflowNodeKind.Start, "kickoff", 1, null,
                    new[] { "Completed" }, 0, 0),
                new WorkflowNode(loopId, WorkflowNodeKind.ReviewLoop, AgentKey: null,
                    AgentVersion: null, OutputScript: null,
                    OutputPorts: new[] { "Approved" },
                    LayoutX: 0, LayoutY: 0,
                    SubflowKey: "child", SubflowVersion: 1,
                    ReviewMaxRounds: 3),
            },
            Edges: new[]
            {
                new WorkflowEdge(startId, "Completed", loopId, WorkflowEdge.DefaultInputPort, false, 0),
            },
            Inputs: Array.Empty<WorkflowInput>());

        workflow.TerminalPorts.Should().BeEquivalentTo(new[] { "Approved", "Exhausted" });
    }

    [Fact]
    public void TerminalPorts_ShouldOmitReviewLoopExhaustedWhenWired()
    {
        var startId = Guid.NewGuid();
        var loopId = Guid.NewGuid();
        var exhaustedHandlerId = Guid.NewGuid();

        var workflow = new Workflow(
            Key: "rl-wired",
            Version: 1,
            Name: "ReviewLoop wired",
            MaxRoundsPerRound: 1,
            CreatedAtUtc: DateTime.UtcNow,
            Nodes: new[]
            {
                new WorkflowNode(startId, WorkflowNodeKind.Start, "kickoff", 1, null,
                    new[] { "Completed" }, 0, 0),
                new WorkflowNode(loopId, WorkflowNodeKind.ReviewLoop, AgentKey: null,
                    AgentVersion: null, OutputScript: null,
                    OutputPorts: new[] { "Approved" },
                    LayoutX: 0, LayoutY: 0,
                    SubflowKey: "child", SubflowVersion: 1,
                    ReviewMaxRounds: 3),
                new WorkflowNode(exhaustedHandlerId, WorkflowNodeKind.Agent, "fallback", 1, null,
                    new[] { "Done" }, 0, 0),
            },
            Edges: new[]
            {
                new WorkflowEdge(startId, "Completed", loopId, WorkflowEdge.DefaultInputPort, false, 0),
                new WorkflowEdge(loopId, "Exhausted", exhaustedHandlerId, WorkflowEdge.DefaultInputPort, false, 1),
            },
            Inputs: Array.Empty<WorkflowInput>());

        workflow.TerminalPorts.Should().BeEquivalentTo(new[] { "Approved", "Done" });
    }
}
