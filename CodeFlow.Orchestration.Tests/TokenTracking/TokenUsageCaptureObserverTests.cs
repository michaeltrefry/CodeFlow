using System.Collections.Concurrent;
using System.Text.Json;
using CodeFlow.Orchestration.TokenTracking;
using CodeFlow.Persistence;
using CodeFlow.Runtime;
using FluentAssertions;

namespace CodeFlow.Orchestration.Tests.TokenTracking;

public sealed class TokenUsageCaptureObserverTests
{
    [Fact]
    public async Task OnModelCallCompletedAsync_WhenRawUsagePresent_WritesOneRecordWithVerbatimUsageAndAttribution()
    {
        // Slice 2 acceptance: a real Responses-style call producing a `usage` payload must end
        // up as exactly one TokenUsageRecord with the provider's fields preserved verbatim,
        // attributed to the saga-side TraceId / NodeId / scope chain pre-resolved by the
        // consumer.
        var repo = new RecordingTokenUsageRecordRepository();
        var rootTraceId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var scope1 = Guid.NewGuid();
        var scope2 = Guid.NewGuid();
        var fixedNow = new DateTime(2026, 4, 28, 12, 0, 0, DateTimeKind.Utc);

        var observer = new TokenUsageCaptureObserver(
            repo,
            rootTraceId: rootTraceId,
            nodeId: nodeId,
            scopeChain: new[] { scope1, scope2 },
            nowProvider: () => fixedNow);

        using var rawDoc = JsonDocument.Parse("""
        {
            "input_tokens": 100,
            "output_tokens": 50,
            "total_tokens": 150,
            "input_tokens_details": { "cached_tokens": 25 },
            "output_tokens_details": { "reasoning_tokens": 12 },
            "some_future_field": "future_value"
        }
        """);
        var raw = rawDoc.RootElement.Clone();

        var invocationId = Guid.NewGuid();
        await observer.OnModelCallCompletedAsync(
            invocationId: invocationId,
            roundNumber: 1,
            responseMessage: new ChatMessage(ChatMessageRole.Assistant, "ok"),
            callTokenUsage: new Runtime.TokenUsage(100, 50, 150),
            cumulativeTokenUsage: new Runtime.TokenUsage(100, 50, 150),
            provider: "openai",
            model: "gpt-5",
            rawUsage: raw,
            cancellationToken: CancellationToken.None);

        repo.Records.Should().ContainSingle();
        var record = repo.Records[0];
        record.TraceId.Should().Be(rootTraceId);
        record.NodeId.Should().Be(nodeId);
        record.InvocationId.Should().Be(invocationId);
        record.ScopeChain.Should().Equal(scope1, scope2);
        record.Provider.Should().Be("openai");
        record.Model.Should().Be("gpt-5");
        record.RecordedAtUtc.Should().Be(fixedNow);

        // Verbatim usage — the raw JSON survives the observer hop unchanged. Slices 6/7/8
        // depend on this being present in the persisted form, not synthesized.
        record.Usage.GetProperty("input_tokens").GetInt32().Should().Be(100);
        record.Usage.GetProperty("output_tokens").GetInt32().Should().Be(50);
        record.Usage.GetProperty("input_tokens_details").GetProperty("cached_tokens").GetInt32().Should().Be(25);
        record.Usage.GetProperty("output_tokens_details").GetProperty("reasoning_tokens").GetInt32().Should().Be(12);
        record.Usage.GetProperty("some_future_field").GetString().Should().Be("future_value");
    }

    [Fact]
    public async Task OnModelCallCompletedAsync_WhenRawUsageNull_WritesZeroRecords()
    {
        // Capture gate: the observer refuses to synthesize a record from the flat
        // Runtime.TokenUsage alone. That preserves the slice-by-slice progression — Anthropic
        // and LMStudio land in slices 3/4 and only THEN start producing records. A premature
        // record with input_tokens/output_tokens but no cache or reasoning fields would
        // permanently lose information once the call resolves.
        var repo = new RecordingTokenUsageRecordRepository();
        var observer = new TokenUsageCaptureObserver(
            repo,
            rootTraceId: Guid.NewGuid(),
            nodeId: Guid.NewGuid(),
            scopeChain: Array.Empty<Guid>());

        await observer.OnModelCallCompletedAsync(
            invocationId: Guid.NewGuid(),
            roundNumber: 1,
            responseMessage: new ChatMessage(ChatMessageRole.Assistant, "ok"),
            callTokenUsage: new Runtime.TokenUsage(50, 20, 70),
            cumulativeTokenUsage: new Runtime.TokenUsage(50, 20, 70),
            provider: "anthropic",
            model: "claude-opus-4-7",
            rawUsage: null,
            cancellationToken: CancellationToken.None);

        repo.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task ToolHooks_AreNoOps_OnlyModelCallCompletionDrivesPersistence()
    {
        // Token-usage records are tied to LLM round-trips, not tool calls. Tool hook fan-out
        // would create one record per tool call and obliterate per-round attribution.
        var repo = new RecordingTokenUsageRecordRepository();
        var observer = new TokenUsageCaptureObserver(
            repo,
            rootTraceId: Guid.NewGuid(),
            nodeId: Guid.NewGuid(),
            scopeChain: Array.Empty<Guid>());

        await observer.OnModelCallStartedAsync(Guid.NewGuid(), 1, CancellationToken.None);
        await observer.OnToolCallStartedAsync(new ToolCall("c1", "tool", null), CancellationToken.None);
        await observer.OnToolCallCompletedAsync(
            new ToolCall("c1", "tool", null),
            new ToolResult("c1", "result"),
            CancellationToken.None);

        repo.Records.Should().BeEmpty();
    }

    private sealed class RecordingTokenUsageRecordRepository : ITokenUsageRecordRepository
    {
        private readonly ConcurrentBag<TokenUsageRecord> records = new();

        public IReadOnlyList<TokenUsageRecord> Records => records.ToArray();

        public Task AddAsync(TokenUsageRecord record, CancellationToken cancellationToken = default)
        {
            records.Add(record);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TokenUsageRecord>> ListByTraceAsync(Guid traceId, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<TokenUsageRecord> filtered = records.Where(r => r.TraceId == traceId).ToArray();
            return Task.FromResult(filtered);
        }
    }
}
