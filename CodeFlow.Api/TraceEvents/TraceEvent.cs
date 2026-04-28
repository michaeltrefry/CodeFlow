using System.Text.Json;

namespace CodeFlow.Api.TraceEvents;

public enum TraceEventKind
{
    Requested = 0,
    Completed = 1,
    TokenUsageRecorded = 2
}

/// <summary>
/// Payload for a <see cref="TraceEventKind.TokenUsageRecorded"/> trace event. Mirrors the
/// persisted <c>TokenUsageRecord</c> the SSE clients need to render incremental usage overlays
/// in the trace inspector. <see cref="Usage"/> is the provider-reported usage object verbatim.
/// </summary>
public sealed record TokenUsageEventPayload(
    Guid RecordId,
    Guid NodeId,
    Guid InvocationId,
    IReadOnlyList<Guid> ScopeChain,
    string Provider,
    string Model,
    JsonElement Usage);

public sealed record TraceEvent(
    Guid TraceId,
    Guid RoundId,
    TraceEventKind Kind,
    string AgentKey,
    int AgentVersion,
    Uri? OutputRef,
    Uri? InputRef,
    string? Decision,
    JsonElement? DecisionPayload,
    DateTimeOffset TimestampUtc,
    // Populated only for TraceEventKind.TokenUsageRecorded. Existing kinds keep the field null
    // so SSE clients that don't know about it can ignore it; the JSON serialization stays
    // additive and backwards-compatible.
    TokenUsageEventPayload? TokenUsage = null);
