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
                    RepoSlug: "example/repo"),
                Repositories:
                [
                    new ToolRepositoryContext(
                        "example",
                        "repo",
                        Url: "https://github.com/example/repo.git",
                        RepoIdentityKey: "github.com/example/repo",
                        RepoSlug: "example/repo")
                ]));

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
              "portName": "Rejected",
              "payload": { "reasons": ["Needs stronger citations"], "severity": "medium" }
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
        JsonElement.DeepEquals(roundTripped.DecisionPayload!.Value, decisionPayload).Should().BeTrue();
        roundTripped.Duration.Should().Be(TimeSpan.FromSeconds(4.5));
        roundTripped.TokenUsage.Should().BeEquivalentTo(new TokenUsage(120, 45, 165));
    }

    [Fact]
    public void SubflowInvokeRequested_ShouldRoundTripThroughJsonSerialization()
    {
        var workflowContext = new Dictionary<string, JsonElement>
        {
            ["target"] = JsonDocument.Parse("""{"path":"/repo"}""").RootElement.Clone(),
            ["initialRequest"] = JsonDocument.Parse("\"build a blog\"").RootElement.Clone()
        };

        var message = new SubflowInvokeRequested(
            ParentTraceId: Guid.NewGuid(),
            ParentNodeId: Guid.NewGuid(),
            ParentRoundId: Guid.NewGuid(),
            ChildTraceId: Guid.NewGuid(),
            SubflowKey: "shared-utility",
            SubflowVersion: 7,
            InputRef: new Uri("file:///tmp/codeflow/parent/output.bin"),
            WorkflowContext: workflowContext,
            Depth: 1);

        var json = JsonSerializer.Serialize(message, SerializerOptions);
        var roundTripped = JsonSerializer.Deserialize<SubflowInvokeRequested>(json, SerializerOptions);

        roundTripped.Should().NotBeNull();
        roundTripped!.ParentTraceId.Should().Be(message.ParentTraceId);
        roundTripped.ParentNodeId.Should().Be(message.ParentNodeId);
        roundTripped.ParentRoundId.Should().Be(message.ParentRoundId);
        roundTripped.ChildTraceId.Should().Be(message.ChildTraceId);
        roundTripped.SubflowKey.Should().Be("shared-utility");
        roundTripped.SubflowVersion.Should().Be(7);
        roundTripped.InputRef.Should().Be(message.InputRef);
        roundTripped.Depth.Should().Be(1);
        roundTripped.WorkflowContext.Should().ContainKeys("target", "initialRequest");
    }

    [Fact]
    public void SubflowCompleted_ShouldRoundTripThroughJsonSerialization()
    {
        var workflowContext = new Dictionary<string, JsonElement>
        {
            ["resolvedSpec"] = JsonDocument.Parse("""{"engine":"markdown"}""").RootElement.Clone()
        };

        var message = new SubflowCompleted(
            ParentTraceId: Guid.NewGuid(),
            ParentNodeId: Guid.NewGuid(),
            ParentRoundId: Guid.NewGuid(),
            ChildTraceId: Guid.NewGuid(),
            OutputPortName: "Completed",
            OutputRef: new Uri("file:///tmp/codeflow/child/final.bin"),
            WorkflowContext: workflowContext);

        var json = JsonSerializer.Serialize(message, SerializerOptions);
        var roundTripped = JsonSerializer.Deserialize<SubflowCompleted>(json, SerializerOptions);

        roundTripped.Should().NotBeNull();
        roundTripped!.ParentTraceId.Should().Be(message.ParentTraceId);
        roundTripped.ParentNodeId.Should().Be(message.ParentNodeId);
        roundTripped.ParentRoundId.Should().Be(message.ParentRoundId);
        roundTripped.ChildTraceId.Should().Be(message.ChildTraceId);
        roundTripped.OutputPortName.Should().Be("Completed");
        roundTripped.OutputRef.Should().Be(message.OutputRef);
        roundTripped.WorkflowContext.Should().ContainKey("resolvedSpec");
        roundTripped.WorkflowContext["resolvedSpec"].GetProperty("engine").GetString().Should().Be("markdown");
    }

    [Theory]
    [InlineData("Completed")]
    [InlineData("Failed")]
    [InlineData("Escalated")]
    public void SubflowCompleted_ShouldRoundTripEachTerminalOutputPort(string portName)
    {
        var message = new SubflowCompleted(
            ParentTraceId: Guid.NewGuid(),
            ParentNodeId: Guid.NewGuid(),
            ParentRoundId: Guid.NewGuid(),
            ChildTraceId: Guid.NewGuid(),
            OutputPortName: portName,
            OutputRef: new Uri("file:///tmp/codeflow/child/final.bin"),
            WorkflowContext: new Dictionary<string, JsonElement>());

        var json = JsonSerializer.Serialize(message, SerializerOptions);
        var roundTripped = JsonSerializer.Deserialize<SubflowCompleted>(json, SerializerOptions);

        roundTripped!.OutputPortName.Should().Be(portName);
    }

    [Fact]
    public void SubflowInvokeRequested_ShouldRoundTripReviewLoopRoundFields()
    {
        // ReviewLoop parent populates ReviewRound + ReviewMaxRounds on dispatch; plain
        // Subflow invocations leave them null. Both shapes must survive JSON round-trip.
        var message = new SubflowInvokeRequested(
            ParentTraceId: Guid.NewGuid(),
            ParentNodeId: Guid.NewGuid(),
            ParentRoundId: Guid.NewGuid(),
            ChildTraceId: Guid.NewGuid(),
            SubflowKey: "draft-critique-revise",
            SubflowVersion: 2,
            InputRef: new Uri("file:///tmp/codeflow/parent/output.bin"),
            WorkflowContext: new Dictionary<string, JsonElement>(),
            Depth: 1,
            ReviewRound: 2,
            ReviewMaxRounds: 3);

        var json = JsonSerializer.Serialize(message, SerializerOptions);
        var roundTripped = JsonSerializer.Deserialize<SubflowInvokeRequested>(json, SerializerOptions);

        roundTripped!.ReviewRound.Should().Be(2);
        roundTripped.ReviewMaxRounds.Should().Be(3);
    }

    [Fact]
    public void SubflowInvokeRequested_ShouldDefaultReviewLoopFieldsToNullForPlainSubflow()
    {
        var message = new SubflowInvokeRequested(
            ParentTraceId: Guid.NewGuid(),
            ParentNodeId: Guid.NewGuid(),
            ParentRoundId: Guid.NewGuid(),
            ChildTraceId: Guid.NewGuid(),
            SubflowKey: "shared-utility",
            SubflowVersion: 7,
            InputRef: new Uri("file:///tmp/codeflow/parent/output.bin"),
            WorkflowContext: new Dictionary<string, JsonElement>(),
            Depth: 1);

        var json = JsonSerializer.Serialize(message, SerializerOptions);
        var roundTripped = JsonSerializer.Deserialize<SubflowInvokeRequested>(json, SerializerOptions);

        roundTripped!.ReviewRound.Should().BeNull("plain Subflow invocations must leave ReviewRound null");
        roundTripped.ReviewMaxRounds.Should().BeNull();
    }

    [Theory]
    [InlineData("Approved")]
    [InlineData("Rejected")]
    [InlineData("Completed")]
    [InlineData("Failed")]
    [InlineData("CustomBranch")]
    public void SubflowCompleted_ShouldRoundTripTerminalDecisionKind(string decision)
    {
        // ReviewLoop parents need the child's terminal decision on SubflowCompleted so they can
        // drive their outcome mapping without re-reading saga state. Plain Subflow completions
        // omit the field, which must deserialize to null. Decision is now an arbitrary
        // author-defined port name string.
        var message = new SubflowCompleted(
            ParentTraceId: Guid.NewGuid(),
            ParentNodeId: Guid.NewGuid(),
            ParentRoundId: Guid.NewGuid(),
            ChildTraceId: Guid.NewGuid(),
            OutputPortName: decision,
            OutputRef: new Uri("file:///tmp/codeflow/child/final.bin"),
            WorkflowContext: new Dictionary<string, JsonElement>(),
            Decision: decision);

        var json = JsonSerializer.Serialize(message, SerializerOptions);
        var roundTripped = JsonSerializer.Deserialize<SubflowCompleted>(json, SerializerOptions);

        roundTripped!.Decision.Should().Be(decision);
    }

    [Fact]
    public void SubflowCompleted_ShouldDefaultDecisionToNullWhenOmitted()
    {
        var message = new SubflowCompleted(
            ParentTraceId: Guid.NewGuid(),
            ParentNodeId: Guid.NewGuid(),
            ParentRoundId: Guid.NewGuid(),
            ChildTraceId: Guid.NewGuid(),
            OutputPortName: "Completed",
            OutputRef: new Uri("file:///tmp/codeflow/child/final.bin"),
            WorkflowContext: new Dictionary<string, JsonElement>());

        var json = JsonSerializer.Serialize(message, SerializerOptions);
        var roundTripped = JsonSerializer.Deserialize<SubflowCompleted>(json, SerializerOptions);

        roundTripped!.Decision.Should().BeNull("plain Subflow completions leave Decision unset");
    }

}
