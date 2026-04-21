using FluentAssertions;
using System.Text.Json;

namespace CodeFlow.Persistence.Tests;

public sealed class WorkflowNodeTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void WorkflowNode_ShouldSupportValueEquality()
    {
        var id = Guid.NewGuid();
        var ports = new[] { "Completed", "Rejected" };

        var a = new WorkflowNode(id, WorkflowNodeKind.Agent, "reviewer", 2, null, ports, 120, 240);
        var b = new WorkflowNode(id, WorkflowNodeKind.Agent, "reviewer", 2, null, ports, 120, 240);

        a.Should().Be(b);
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void WorkflowNode_WithDifferentKind_ShouldNotBeEqual()
    {
        var id = Guid.NewGuid();

        var agentNode = new WorkflowNode(id, WorkflowNodeKind.Agent, "reviewer", 1, null, Array.Empty<string>(), 0, 0);
        var logicNode = agentNode with { Kind = WorkflowNodeKind.Logic };

        agentNode.Should().NotBe(logicNode);
    }

    [Fact]
    public void WorkflowNode_WithImmutableUpdate_ShouldReturnNewInstance()
    {
        var node = new WorkflowNode(
            Guid.NewGuid(),
            WorkflowNodeKind.Logic,
            AgentKey: null,
            AgentVersion: null,
            Script: "setNodePath('A');",
            OutputPorts: new[] { "A", "B" },
            LayoutX: 10,
            LayoutY: 20);

        var moved = node with { LayoutX = 99 };

        moved.LayoutX.Should().Be(99);
        moved.Script.Should().Be(node.Script);
        moved.OutputPorts.Should().BeEquivalentTo(node.OutputPorts);
        moved.Should().NotBe(node);
    }

    [Fact]
    public void WorkflowNode_ShouldRoundTripThroughJsonSerialization()
    {
        var node = new WorkflowNode(
            Id: Guid.NewGuid(),
            Kind: WorkflowNodeKind.Logic,
            AgentKey: null,
            AgentVersion: null,
            Script: "if (input.kind === 'NewProject') { setNodePath('NewProjectFlow'); }",
            OutputPorts: new[] { "NewProjectFlow", "FeatureFlow", "BugFixFlow" },
            LayoutX: 150.5,
            LayoutY: 320.25);

        var json = JsonSerializer.Serialize(node, SerializerOptions);
        var roundTripped = JsonSerializer.Deserialize<WorkflowNode>(json, SerializerOptions);

        roundTripped.Should().NotBeNull();
        roundTripped!.Id.Should().Be(node.Id);
        roundTripped.Kind.Should().Be(WorkflowNodeKind.Logic);
        roundTripped.AgentKey.Should().BeNull();
        roundTripped.AgentVersion.Should().BeNull();
        roundTripped.Script.Should().Be(node.Script);
        roundTripped.OutputPorts.Should().Equal(node.OutputPorts);
        roundTripped.LayoutX.Should().Be(150.5);
        roundTripped.LayoutY.Should().Be(320.25);
    }

    [Fact]
    public void WorkflowNode_AgentKindRoundTrip_ShouldPreserveAgentReference()
    {
        var node = new WorkflowNode(
            Id: Guid.NewGuid(),
            Kind: WorkflowNodeKind.Agent,
            AgentKey: "reviewer",
            AgentVersion: 4,
            Script: null,
            OutputPorts: new[] { "Completed", "Approved", "Rejected", "Failed" },
            LayoutX: 0,
            LayoutY: 0);

        var json = JsonSerializer.Serialize(node, SerializerOptions);
        var roundTripped = JsonSerializer.Deserialize<WorkflowNode>(json, SerializerOptions);

        roundTripped!.Kind.Should().Be(WorkflowNodeKind.Agent);
        roundTripped.AgentKey.Should().Be("reviewer");
        roundTripped.AgentVersion.Should().Be(4);
        roundTripped.Script.Should().BeNull();
    }
}
