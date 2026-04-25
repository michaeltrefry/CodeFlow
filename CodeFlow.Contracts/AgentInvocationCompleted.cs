using System.Text.Json;

namespace CodeFlow.Contracts;

public sealed record AgentInvocationCompleted(
    Guid TraceId,
    Guid RoundId,
    Guid FromNodeId,
    string AgentKey,
    int AgentVersion,
    string OutputPortName,
    Uri OutputRef,
    JsonElement? DecisionPayload,
    TimeSpan Duration,
    TokenUsage TokenUsage,
    IReadOnlyDictionary<string, JsonElement>? ContextUpdates = null,
    IReadOnlyDictionary<string, JsonElement>? GlobalUpdates = null);
