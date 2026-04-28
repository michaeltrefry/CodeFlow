using System.Text.Json;
using CodeFlow.Persistence;
using CodeFlow.Runtime;
using MassTransit;
using TokenUsageRecorded = CodeFlow.Contracts.TokenUsageRecorded;

namespace CodeFlow.Orchestration.TokenTracking;

/// <summary>
/// <see cref="IInvocationObserver"/> that persists one <see cref="TokenUsageRecord"/> per
/// resolved LLM round-trip. Built per <c>AgentInvocationConsumer.Consume()</c> call so the
/// per-trace attribution data (root TraceId, NodeId, scope chain) is captured at construction
/// time and reused for every round in this consumer call.
/// </summary>
/// <remarks>
/// <para>
/// Capture gate: a record is written only when the model client surfaced
/// <c>InvocationResponse.RawUsage</c>. The flat <see cref="TokenUsage"/> alone would lose
/// cache_creation_input_tokens / cache_read_input_tokens / reasoning_tokens / future provider
/// fields, so we refuse to synthesize a partial record from it. Slice 2 enables raw usage for
/// OpenAI + LMStudio (OpenAI-compat base); slice 3 wires Anthropic.
/// </para>
/// <para>
/// Replay safety: the observer is wired only inside <c>AgentInvocationConsumer</c>. Replay paths
/// (<c>DryRunExecutor</c>) never enter the consumer or <c>InvocationLoop</c> — they dequeue
/// pre-recorded mocks directly — so no defensive flag is needed at the observer layer.
/// </para>
/// </remarks>
public sealed class TokenUsageCaptureObserver : IInvocationObserver
{
    private readonly ITokenUsageRecordRepository repository;
    private readonly IPublishEndpoint? publishEndpoint;
    private readonly Guid rootTraceId;
    private readonly Guid nodeId;
    private readonly IReadOnlyList<Guid> scopeChain;
    private readonly Func<DateTime> nowProvider;

    public TokenUsageCaptureObserver(
        ITokenUsageRecordRepository repository,
        Guid rootTraceId,
        Guid nodeId,
        IReadOnlyList<Guid> scopeChain,
        IPublishEndpoint? publishEndpoint = null,
        Func<DateTime>? nowProvider = null)
    {
        this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
        // Optional so unit tests that don't spin up a MassTransit harness keep working. Production
        // wires the inbound `ConsumeContext` (which itself implements IPublishEndpoint), so every
        // captured record fans out as a `TokenUsageRecorded` event for the realtime SSE channel.
        this.publishEndpoint = publishEndpoint;
        this.rootTraceId = rootTraceId;
        this.nodeId = nodeId;
        this.scopeChain = scopeChain ?? Array.Empty<Guid>();
        this.nowProvider = nowProvider ?? (() => DateTime.UtcNow);
    }

    public Task OnModelCallStartedAsync(Guid invocationId, int roundNumber, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public async Task OnModelCallCompletedAsync(
        Guid invocationId,
        int roundNumber,
        ChatMessage responseMessage,
        TokenUsage? callTokenUsage,
        TokenUsage? cumulativeTokenUsage,
        string provider,
        string model,
        JsonElement? rawUsage,
        CancellationToken cancellationToken)
    {
        if (rawUsage is not { } usage)
        {
            return;
        }

        var record = new TokenUsageRecord(
            Id: Guid.NewGuid(),
            TraceId: rootTraceId,
            NodeId: nodeId,
            InvocationId: invocationId,
            ScopeChain: scopeChain,
            Provider: provider ?? string.Empty,
            Model: model ?? string.Empty,
            RecordedAtUtc: nowProvider(),
            Usage: usage);

        await repository.AddAsync(record, cancellationToken);

        if (publishEndpoint is not null)
        {
            // Publish AFTER the row is persisted so any consumer reading from the DB on receipt
            // sees the same record. Slice 5: the API-side TraceTokenUsageRecordedObserver picks
            // this up and fans it out onto the SSE channel for the trace inspector.
            await publishEndpoint.Publish(
                new TokenUsageRecorded(
                    TraceId: record.TraceId,
                    RecordId: record.Id,
                    NodeId: record.NodeId,
                    InvocationId: record.InvocationId,
                    ScopeChain: record.ScopeChain,
                    Provider: record.Provider,
                    Model: record.Model,
                    RecordedAtUtc: record.RecordedAtUtc,
                    Usage: record.Usage),
                cancellationToken);
        }
    }

    public Task OnToolCallStartedAsync(ToolCall call, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task OnToolCallCompletedAsync(ToolCall call, ToolResult result, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
