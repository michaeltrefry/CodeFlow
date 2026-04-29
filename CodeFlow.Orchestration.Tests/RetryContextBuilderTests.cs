using System.Text.Json;
using System.Text.Json.Nodes;
using CodeFlow.Orchestration;
using FluentAssertions;

namespace CodeFlow.Orchestration.Tests;

public sealed class RetryContextBuilderTests
{
    private static IRetryContextBuilder NewBuilder() => new RetryContextBuilder();

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void Build_NullPayload_ReasonsAndSummaryNull()
    {
        var snapshot = NewBuilder().Build(attemptNumber: 1, decisionPayload: null);

        snapshot.AttemptNumber.Should().Be(1);
        snapshot.PriorFailureReason.Should().BeNull();
        snapshot.PriorAttemptSummary.Should().BeNull();
    }

    [Fact]
    public void Build_PrefersFailureContextReasonOverNestedPayloadOverTopLevel()
    {
        var payload = Parse("""
        {
          "reason": "top-level",
          "payload": { "reason": "nested-payload" },
          "failure_context": { "reason": "failure-context" }
        }
        """);

        var snapshot = NewBuilder().Build(attemptNumber: 2, decisionPayload: payload);

        snapshot.PriorFailureReason.Should().Be("failure-context");
    }

    [Fact]
    public void Build_FallsBackToNestedPayloadReason_WhenFailureContextHasNoReason()
    {
        var payload = Parse("""
        {
          "reason": "top-level",
          "payload": { "reason": "nested-payload" },
          "failure_context": { }
        }
        """);

        var snapshot = NewBuilder().Build(attemptNumber: 3, decisionPayload: payload);

        snapshot.PriorFailureReason.Should().Be("nested-payload");
    }

    [Fact]
    public void Build_FallsBackToTopLevelReason_WhenNoFailureContextOrPayload()
    {
        var payload = Parse("""{ "reason": "only-top-level" }""");

        var snapshot = NewBuilder().Build(attemptNumber: 4, decisionPayload: payload);

        snapshot.PriorFailureReason.Should().Be("only-top-level");
    }

    [Fact]
    public void Build_BuildsSummaryFromLastOutputAndToolCalls()
    {
        var payload = Parse("""
        {
          "failure_context": {
            "reason": "boom",
            "last_output": "  partial response  ",
            "tool_calls_executed": 3
          }
        }
        """);

        var snapshot = NewBuilder().Build(attemptNumber: 5, decisionPayload: payload);

        snapshot.PriorFailureReason.Should().Be("boom");
        snapshot.PriorAttemptSummary.Should()
            .Contain("Last output: partial response")
            .And.Contain("Tool calls executed: 3");
    }

    [Fact]
    public void Build_OmitsSummary_WhenLastOutputAndToolCallsBothMissing()
    {
        var payload = Parse("""
        { "failure_context": { "reason": "no-detail" } }
        """);

        var snapshot = NewBuilder().Build(attemptNumber: 6, decisionPayload: payload);

        snapshot.PriorAttemptSummary.Should().BeNull();
    }

    [Fact]
    public void ToContract_ProjectsAllFields()
    {
        var snapshot = new RetryContextSnapshot(7, "reason", "summary");
        var contract = RetryContextBuilder.ToContract(snapshot);
        contract.AttemptNumber.Should().Be(7);
        contract.PriorFailureReason.Should().Be("reason");
        contract.PriorAttemptSummary.Should().Be("summary");
    }

    [Fact]
    public void ToJsonNode_OmitsNullFields()
    {
        var snapshot = new RetryContextSnapshot(8, null, null);
        var node = RetryContextBuilder.ToJsonNode(snapshot);
        node["attemptNumber"]!.GetValue<int>().Should().Be(8);
        node.AsObject().ContainsKey("priorFailureReason").Should().BeFalse();
        node.AsObject().ContainsKey("priorAttemptSummary").Should().BeFalse();
    }

    [Fact]
    public void ToMessage_FormatsAttemptReasonAndSummary()
    {
        var msg = RetryContextBuilder.ToMessage(new RetryContextSnapshot(2, "kaboom", "tail"));
        msg.Should().Be("Saga would inject RetryContext: attempt #2. Reason: kaboom. Summary: tail");
    }

    [Fact]
    public void ToMessage_OmitsReasonAndSummary_WhenNullOrWhitespace()
    {
        var msg = RetryContextBuilder.ToMessage(new RetryContextSnapshot(1, null, "   "));
        msg.Should().Be("Saga would inject RetryContext: attempt #1.");
    }

    [Fact]
    public void AsJsonElement_RoundTripsJsonNode()
    {
        var node = new JsonObject { ["a"] = 1, ["b"] = "x" };
        var element = RetryContextBuilder.AsJsonElement(node);
        element.Should().NotBeNull();
        element!.Value.GetProperty("a").GetInt32().Should().Be(1);
        element.Value.GetProperty("b").GetString().Should().Be("x");
    }

    [Fact]
    public void AsJsonElement_NullIn_NullOut()
    {
        RetryContextBuilder.AsJsonElement(null).Should().BeNull();
    }
}
