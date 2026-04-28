using System.Text.Json;
using CodeFlow.Api.Assistant.Tools;
using FluentAssertions;

namespace CodeFlow.Api.Tests.Assistant;

/// <summary>
/// Unit tests for <see cref="AssistantToolDispatcher"/> — focused on the dispatch surface
/// (lookup, oversized result truncation, exception wrapping) so the assistant chat loop can
/// rely on it never throwing.
/// </summary>
public sealed class AssistantToolDispatcherTests
{
    private static readonly JsonElement EmptyArgs = JsonDocument.Parse("{}").RootElement;

    [Fact]
    public void Constructor_DuplicateToolName_Throws()
    {
        var act = () => new AssistantToolDispatcher(new[]
        {
            (IAssistantTool)new RecordingTool("dup", "{}"),
            new RecordingTool("dup", "{}"),
        });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Duplicate*dup*");
    }

    [Fact]
    public async Task Invoke_UnknownTool_ReturnsErrorResult()
    {
        var dispatcher = new AssistantToolDispatcher(new[] { (IAssistantTool)new RecordingTool("known", "{}") });

        var result = await dispatcher.InvokeAsync("nope", EmptyArgs, CancellationToken.None);

        result.IsError.Should().BeTrue();
        // System.Text.Json escapes apostrophes (') by default; match around the quoting.
        result.ResultJson.Should().Contain("Unknown tool");
        result.ResultJson.Should().Contain("nope");
        result.ResultJson.Should().Contain("known");
    }

    [Fact]
    public async Task Invoke_ToolThrows_WrapsExceptionWithoutPropagating()
    {
        var dispatcher = new AssistantToolDispatcher(new[] { (IAssistantTool)new ThrowingTool() });

        var result = await dispatcher.InvokeAsync("throw", EmptyArgs, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.ResultJson.Should().Contain("InvalidOperationException");
        result.ResultJson.Should().Contain("boom");
    }

    [Fact]
    public async Task Invoke_OversizedResult_ReplacedWithError()
    {
        var oversized = new string('x', AssistantToolDispatcher.MaxResultBytes + 1);
        var dispatcher = new AssistantToolDispatcher(new[] { (IAssistantTool)new RecordingTool("big", $"\"{oversized}\"") });

        var result = await dispatcher.InvokeAsync("big", EmptyArgs, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.ResultJson.Should().Contain("exceeds the");
        result.ResultJson.Should().Contain($"{AssistantToolDispatcher.MaxResultBytes}-byte cap");
    }

    [Fact]
    public async Task Invoke_HappyPath_ForwardsResultVerbatim()
    {
        var dispatcher = new AssistantToolDispatcher(new[] { (IAssistantTool)new RecordingTool("ok", "{\"hello\":\"world\"}") });

        var result = await dispatcher.InvokeAsync("ok", EmptyArgs, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.ResultJson.Should().Be("{\"hello\":\"world\"}");
    }

    [Fact]
    public async Task Invoke_OperationCanceledException_PropagatesUnwrapped()
    {
        // Cancellation must propagate so the chat loop sees it and tears down the SSE stream
        // — we don't want to swallow it into a tool-error result.
        var dispatcher = new AssistantToolDispatcher(new[] { (IAssistantTool)new CancelingTool() });

        var act = async () => await dispatcher.InvokeAsync("cancel", EmptyArgs, CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class RecordingTool(string name, string resultJson) : IAssistantTool
    {
        public string Name => name;
        public string Description => "test";
        public JsonElement InputSchema => EmptyArgs;
        public Task<AssistantToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
            => Task.FromResult(new AssistantToolResult(resultJson));
    }

    private sealed class ThrowingTool : IAssistantTool
    {
        public string Name => "throw";
        public string Description => "throws";
        public JsonElement InputSchema => EmptyArgs;
        public Task<AssistantToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
            => throw new InvalidOperationException("boom");
    }

    private sealed class CancelingTool : IAssistantTool
    {
        public string Name => "cancel";
        public string Description => "cancels";
        public JsonElement InputSchema => EmptyArgs;
        public Task<AssistantToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
            => throw new OperationCanceledException();
    }
}
