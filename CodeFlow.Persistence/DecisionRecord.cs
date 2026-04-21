using CodeFlow.Runtime;
using System.Text.Json;

namespace CodeFlow.Persistence;

public sealed record DecisionRecord(
    string AgentKey,
    int AgentVersion,
    AgentDecisionKind Decision,
    JsonElement? DecisionPayload,
    Guid RoundId,
    DateTime RecordedAtUtc,
    Guid? NodeId = null,
    string? OutputPortName = null);
