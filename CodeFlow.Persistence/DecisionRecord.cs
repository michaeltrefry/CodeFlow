using System.Text.Json;

namespace CodeFlow.Persistence;

public sealed record DecisionRecord(
    string AgentKey,
    int AgentVersion,
    string Decision,
    JsonElement? DecisionPayload,
    Guid RoundId,
    DateTime RecordedAtUtc,
    Guid? NodeId = null,
    string? OutputPortName = null,
    string? InputRef = null,
    string? OutputRef = null);
