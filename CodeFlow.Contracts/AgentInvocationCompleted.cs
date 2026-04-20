using System.Text.Json;

namespace CodeFlow.Contracts;

public sealed record AgentInvocationCompleted(
    Guid TraceId,
    Guid RoundId,
    string AgentKey,
    int AgentVersion,
    Uri OutputRef,
    AgentDecisionKind Decision,
    JsonElement? DecisionPayload,
    TimeSpan Duration,
    TokenUsage TokenUsage);
