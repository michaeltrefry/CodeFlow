using FluentAssertions;
using System.Text.Json;

namespace CodeFlow.Contracts.Tests;

public sealed class ContractSerializationTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void AgentInvokeRequested_ShouldRoundTripThroughJsonSerialization()
    {
        var message = new AgentInvokeRequested(
            TraceId: Guid.NewGuid(),
            RoundId: Guid.NewGuid(),
            WorkflowKey: "article-flow",
            WorkflowVersion: 3,
            AgentKey: "reviewer",
            AgentVersion: 2,
            InputRef: new Uri("file:///tmp/codeflow/trace/input.bin"),
            CorrelationHeaders: new Dictionary<string, string>
            {
                ["x-trace-origin"] = "api",
                ["x-request-id"] = "req-123"
            });

        var json = JsonSerializer.Serialize(message, SerializerOptions);
        var roundTripped = JsonSerializer.Deserialize<AgentInvokeRequested>(json, SerializerOptions);

        roundTripped.Should().NotBeNull();
        roundTripped!.TraceId.Should().Be(message.TraceId);
        roundTripped.RoundId.Should().Be(message.RoundId);
        roundTripped.WorkflowKey.Should().Be("article-flow");
        roundTripped.WorkflowVersion.Should().Be(3);
        roundTripped.AgentKey.Should().Be("reviewer");
        roundTripped.AgentVersion.Should().Be(2);
        roundTripped.InputRef.Should().Be(message.InputRef);
        roundTripped.CorrelationHeaders.Should().BeEquivalentTo(message.CorrelationHeaders);
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
            AgentKey: "reviewer",
            AgentVersion: 2,
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
        roundTripped.AgentKey.Should().Be("reviewer");
        roundTripped.AgentVersion.Should().Be(2);
        roundTripped.OutputRef.Should().Be(message.OutputRef);
        roundTripped.Decision.Should().Be(AgentDecisionKind.Rejected);
        JsonElement.DeepEquals(roundTripped.DecisionPayload!.Value, decisionPayload).Should().BeTrue();
        roundTripped.Duration.Should().Be(TimeSpan.FromSeconds(4.5));
        roundTripped.TokenUsage.Should().BeEquivalentTo(new TokenUsage(120, 45, 165));
    }
}
