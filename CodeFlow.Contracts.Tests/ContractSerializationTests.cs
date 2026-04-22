using FluentAssertions;
using System.Text.Json;

namespace CodeFlow.Contracts.Tests;

public sealed class ContractSerializationTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void AgentInvokeRequested_ShouldRoundTripThroughJsonSerialization()
    {
        var contextInputs = new Dictionary<string, JsonElement>
        {
            ["initialRequest"] = JsonDocument.Parse("\"draft an article\"").RootElement.Clone(),
            ["settings"] = JsonDocument.Parse("""{"tone":"formal"}""").RootElement.Clone()
        };

        var message = new AgentInvokeRequested(
            TraceId: Guid.NewGuid(),
            RoundId: Guid.NewGuid(),
            WorkflowKey: "article-flow",
            WorkflowVersion: 3,
            NodeId: Guid.NewGuid(),
            AgentKey: "reviewer",
            AgentVersion: 2,
            InputRef: new Uri("file:///tmp/codeflow/trace/input.bin"),
            ContextInputs: contextInputs,
            CorrelationHeaders: new Dictionary<string, string>
            {
                ["x-trace-origin"] = "api",
                ["x-request-id"] = "req-123"
            },
            ToolExecutionContext: new ToolExecutionContext(
                new ToolWorkspaceContext(
                    Guid.NewGuid(),
                    "/tmp/codeflow/workspaces/abc123/repo",
                    RepoUrl: "https://github.com/example/repo.git",
                    RepoIdentityKey: "github.com/example/repo",
                    RepoSlug: "example/repo")));

        var json = JsonSerializer.Serialize(message, SerializerOptions);
        var roundTripped = JsonSerializer.Deserialize<AgentInvokeRequested>(json, SerializerOptions);

        roundTripped.Should().NotBeNull();
        roundTripped!.TraceId.Should().Be(message.TraceId);
        roundTripped.RoundId.Should().Be(message.RoundId);
        roundTripped.WorkflowKey.Should().Be("article-flow");
        roundTripped.WorkflowVersion.Should().Be(3);
        roundTripped.NodeId.Should().Be(message.NodeId);
        roundTripped.AgentKey.Should().Be("reviewer");
        roundTripped.AgentVersion.Should().Be(2);
        roundTripped.InputRef.Should().Be(message.InputRef);
        roundTripped.ContextInputs.Should().ContainKeys("initialRequest", "settings");
        roundTripped.CorrelationHeaders.Should().BeEquivalentTo(message.CorrelationHeaders);
        roundTripped.ToolExecutionContext.Should().BeEquivalentTo(message.ToolExecutionContext);
    }

    [Fact]
    public void AgentInvocationCompleted_ShouldRoundTripThroughJsonSerialization()
    {
        var decisionPayload = JsonDocument.Parse(
            """
            {
              "kind": "Rejected",
              "reasons": ["Needs stronger citations"],
              "payload": { "severity": "medium" }
            }
            """).RootElement.Clone();
        var message = new AgentInvocationCompleted(
            TraceId: Guid.NewGuid(),
            RoundId: Guid.NewGuid(),
            FromNodeId: Guid.NewGuid(),
            AgentKey: "reviewer",
            AgentVersion: 2,
            OutputPortName: "Rejected",
            OutputRef: new Uri("file:///tmp/codeflow/trace/output.bin"),
            Decision: AgentDecisionKind.Rejected,
            DecisionPayload: decisionPayload,
            Duration: TimeSpan.FromSeconds(4.5),
            TokenUsage: new TokenUsage(120, 45, 165));

        var json = JsonSerializer.Serialize(message, SerializerOptions);
        var roundTripped = JsonSerializer.Deserialize<AgentInvocationCompleted>(json, SerializerOptions);

        roundTripped.Should().NotBeNull();
        roundTripped!.TraceId.Should().Be(message.TraceId);
        roundTripped.RoundId.Should().Be(message.RoundId);
        roundTripped.FromNodeId.Should().Be(message.FromNodeId);
        roundTripped.AgentKey.Should().Be("reviewer");
        roundTripped.AgentVersion.Should().Be(2);
        roundTripped.OutputPortName.Should().Be("Rejected");
        roundTripped.OutputRef.Should().Be(message.OutputRef);
        roundTripped.Decision.Should().Be(AgentDecisionKind.Rejected);
        JsonElement.DeepEquals(roundTripped.DecisionPayload!.Value, decisionPayload).Should().BeTrue();
        roundTripped.Duration.Should().Be(TimeSpan.FromSeconds(4.5));
        roundTripped.TokenUsage.Should().BeEquivalentTo(new TokenUsage(120, 45, 165));
    }

    [Fact]
    public void AgentDecisionPorts_ToPortName_UsesDecisionKindString()
    {
        AgentDecisionPorts.ToPortName(AgentDecisionKind.Completed).Should().Be("Completed");
        AgentDecisionPorts.ToPortName(AgentDecisionKind.Approved).Should().Be("Approved");
        AgentDecisionPorts.ToPortName(AgentDecisionKind.ApprovedWithActions).Should().Be("ApprovedWithActions");
        AgentDecisionPorts.ToPortName(AgentDecisionKind.Rejected).Should().Be("Rejected");
        AgentDecisionPorts.ToPortName(AgentDecisionKind.Failed).Should().Be("Failed");
    }
}
