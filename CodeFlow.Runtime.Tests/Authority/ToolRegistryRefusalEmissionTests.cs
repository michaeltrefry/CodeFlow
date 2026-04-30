using System.Text.Json.Nodes;
using CodeFlow.Runtime;
using CodeFlow.Runtime.Authority;
using FluentAssertions;

namespace CodeFlow.Runtime.Tests.Authority;

/// <summary>
/// Unit tests for the ToolRegistry refusal-emit hook (sc-285). When a tool returns
/// <see cref="ToolResult.IsError"/> = true with a structured refusal payload, the registered
/// <see cref="IRefusalEventSink"/> receives a corresponding <see cref="RefusalEvent"/>.
/// Non-refusal errors and success results must NOT trigger the sink.
/// </summary>
public sealed class ToolRegistryRefusalEmissionTests
{
    [Fact]
    public async Task InvokeAsync_StructuredRefusalPayload_EmitsRefusalEvent()
    {
        var sink = new RecordingSink();
        var traceId = Guid.NewGuid();
        var registry = NewRegistryWithRefusalProvider(sink);

        var result = await registry.InvokeAsync(
            new ToolCall("c1", "stub_refuse", new JsonObject()),
            policy: null,
            cancellationToken: default,
            context: ContextWithTrace(traceId));

        result.IsError.Should().BeTrue();
        sink.Recorded.Should().HaveCount(1);
        var refusal = sink.Recorded.Single();
        refusal.Code.Should().Be("preimage-mismatch");
        refusal.Reason.Should().StartWith("stale preimage");
        refusal.Axis.Should().Be("workspace-mutation");
        refusal.Path.Should().Be("src/main.txt");
        refusal.TraceId.Should().Be(traceId);
        refusal.Stage.Should().Be(RefusalStages.Tool);
        refusal.DetailJson.Should().Contain("\"expected\"");
    }

    [Fact]
    public async Task InvokeAsync_SuccessResult_DoesNotEmitRefusal()
    {
        var sink = new RecordingSink();
        var registry = NewRegistry(sink, new StubProvider("stub_ok", new ToolResult("c1", """{"ok":true}""")));

        var result = await registry.InvokeAsync(
            new ToolCall("c1", "stub_ok", new JsonObject()),
            policy: null,
            cancellationToken: default,
            context: ContextWithTrace(Guid.NewGuid()));

        result.IsError.Should().BeFalse();
        sink.Recorded.Should().BeEmpty();
    }

    [Fact]
    public async Task InvokeAsync_ErrorWithoutRefusalShape_DoesNotEmitRefusal()
    {
        // Error tool result whose content is not the structured refusal envelope. Sink stays
        // untouched — the producer didn't speak the refusal protocol, so we don't fabricate one.
        var sink = new RecordingSink();
        var registry = NewRegistry(sink, new StubProvider(
            "stub_err",
            new ToolResult("c1", "boom: legacy provider error", IsError: true)));

        var result = await registry.InvokeAsync(
            new ToolCall("c1", "stub_err", new JsonObject()),
            policy: null,
            cancellationToken: default,
            context: ContextWithTrace(Guid.NewGuid()));

        result.IsError.Should().BeTrue();
        sink.Recorded.Should().BeEmpty();
    }

    [Fact]
    public async Task InvokeAsync_SinkThrows_DoesNotPropagateOrLoseToolResult()
    {
        var sink = new ThrowingSink();
        var registry = NewRegistryWithRefusalProvider(sink);

        var result = await registry.InvokeAsync(
            new ToolCall("c1", "stub_refuse", new JsonObject()),
            policy: null,
            cancellationToken: default,
            context: ContextWithTrace(Guid.NewGuid()));

        // Sink failures must never break the calling tool's primary failure flow — the
        // structured payload is already in the ToolResult that reaches the LLM.
        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("preimage-mismatch");
    }

    [Fact]
    public async Task InvokeAsync_MissingWorkspaceContext_StillEmitsWithNullTraceId()
    {
        var sink = new RecordingSink();
        var registry = NewRegistryWithRefusalProvider(sink);

        await registry.InvokeAsync(
            new ToolCall("c1", "stub_refuse", new JsonObject()),
            policy: null,
            cancellationToken: default,
            context: null);

        sink.Recorded.Should().HaveCount(1);
        sink.Recorded.Single().TraceId.Should().BeNull();
    }

    private static ToolRegistry NewRegistryWithRefusalProvider(IRefusalEventSink sink)
    {
        var refusalContent = """
            {
              "ok": false,
              "refusal": {
                "code": "preimage-mismatch",
                "reason": "stale preimage on src/main.txt",
                "axis": "workspace-mutation",
                "path": "src/main.txt",
                "detail": { "expected": "abc", "actual": "def" }
              }
            }
            """;

        return NewRegistry(sink, new StubProvider("stub_refuse", new ToolResult("c1", refusalContent, IsError: true)));
    }

    private static ToolRegistry NewRegistry(IRefusalEventSink sink, IToolProvider provider)
    {
        return new ToolRegistry(new[] { provider }, sink, () => DateTimeOffset.Parse("2026-04-30T12:00:00Z"));
    }

    private static ToolExecutionContext ContextWithTrace(Guid traceId) =>
        new(new ToolWorkspaceContext(traceId, "/tmp/ws"));

    private sealed class StubProvider : IToolProvider
    {
        private readonly string toolName;
        private readonly ToolResult result;

        public StubProvider(string toolName, ToolResult result)
        {
            this.toolName = toolName;
            this.result = result;
        }

        public ToolCategory Category => ToolCategory.Host;

        public IReadOnlyList<ToolSchema> AvailableTools(ToolAccessPolicy policy) =>
            new[] { new ToolSchema(toolName, "stub", new JsonObject()) };

        public Task<ToolResult> InvokeAsync(
            ToolCall toolCall,
            CancellationToken cancellationToken = default,
            ToolExecutionContext? context = null) => Task.FromResult(result);
    }

    private sealed class RecordingSink : IRefusalEventSink
    {
        public List<RefusalEvent> Recorded { get; } = new();

        public Task RecordAsync(RefusalEvent refusal, CancellationToken cancellationToken = default)
        {
            Recorded.Add(refusal);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingSink : IRefusalEventSink
    {
        public Task RecordAsync(RefusalEvent refusal, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("simulated sink failure");
    }
}
