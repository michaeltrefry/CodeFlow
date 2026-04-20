using FluentAssertions;
using System.Text.Json.Nodes;

namespace CodeFlow.Runtime.Tests;

public sealed class RuntimeSmokeTests
{
    [Fact]
    public void RuntimeAssemblyMarker_ShouldBeConstructible()
    {
        var marker = new RuntimeAssemblyMarker();

        marker.Should().NotBeNull();
    }

    [Fact]
    public void InvocationModel_ShouldRepresentToolCallingRequestsAndResponses()
    {
        var toolSchema = new ToolSchema(
            "search_docs",
            "Searches indexed documentation.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["query"] = new JsonObject
                    {
                        ["type"] = "string"
                    }
                }
            });

        var toolCall = new ToolCall(
            "call_123",
            "search_docs",
            new JsonObject
            {
                ["query"] = "MassTransit saga docs"
            });

        var request = new InvocationRequest(
            Messages:
            [
                new ChatMessage(ChatMessageRole.System, "You are a helpful runtime."),
                new ChatMessage(ChatMessageRole.User, "Find the saga docs.")
            ],
            Tools: [toolSchema],
            Model: "test-model",
            MaxTokens: 512,
            Temperature: 0.2);

        var response = new InvocationResponse(
            new ChatMessage(
                ChatMessageRole.Assistant,
                string.Empty,
                ToolCalls: [toolCall]),
            InvocationStopReason.ToolCalls,
            new TokenUsage(10, 5, 15));

        request.Messages.Should().HaveCount(2);
        request.Tools.Should().ContainSingle().Which.Name.Should().Be("search_docs");
        response.StopReason.Should().Be(InvocationStopReason.ToolCalls);
        response.Message.ToolCalls.Should().ContainSingle().Which.Arguments?["query"]?.GetValue<string>()
            .Should().Be("MassTransit saga docs");
    }
}
