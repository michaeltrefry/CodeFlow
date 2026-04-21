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
    AgentDecisionKind Decision,
    JsonElement? DecisionPayload,
    TimeSpan Duration,
    TokenUsage TokenUsage);
