using System.Text.Json;

namespace CodeFlow.Api.TraceEvents;

public enum TraceEventKind
{
    Requested = 0,
    Completed = 1
}

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
    DateTimeOffset TimestampUtc);
