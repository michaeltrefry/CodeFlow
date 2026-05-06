using System.Net.Http;
using CodeFlow.Api.Assistant;
using FluentAssertions;

namespace CodeFlow.Api.Tests.Assistant;

/// <summary>
/// Verifies the chat-service classifier turns transient SSE stream-drop exceptions into a
/// message that points the user at the right next step (retry), instead of leaking the raw
/// "The response ended prematurely. (ResponseEnded)" text from the runtime.
/// </summary>
public sealed class AssistantTurnFailureClassificationTests
{
    [Fact]
    public void HttpIOException_ResponseEnded_RephrasedAsConnectionDrop()
    {
        var ex = new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely.");

        var message = AssistantChatService.ClassifyTurnFailureMessage(ex);

        message.Should().Contain("dropped");
        message.Should().Contain("retry", because: "the user needs the right next step, not a stack-trace term");
        message.Should().NotContain("prematurely",
            because: "the raw runtime phrasing was the original UX problem we're fixing");
    }

    [Fact]
    public void OtherException_FallsBackToRawMessage()
    {
        // Anything we haven't classified should surface its actual message — better than a
        // blanket "Something went wrong" that hides real signal during development.
        var ex = new InvalidOperationException("provider is not configured");

        AssistantChatService.ClassifyTurnFailureMessage(ex).Should().Be("provider is not configured");
    }

    [Fact]
    public void HttpIOException_OtherErrorCode_FallsBackToRawMessage()
    {
        // Only ResponseEnded gets the rephrasing — other HttpRequestError values (UserAuthenticationError,
        // SecureConnectionError, etc.) carry distinct semantics that the user would benefit from seeing.
        var ex = new HttpIOException(HttpRequestError.UserAuthenticationError, "401 Unauthorized");

        var message = AssistantChatService.ClassifyTurnFailureMessage(ex);

        message.Should().Contain("401 Unauthorized",
            because: "the original message must reach the user verbatim when we don't have a better rephrasing");
        message.Should().NotContain("dropped",
            because: "we only rephrase the connection-drop case");
    }
}
