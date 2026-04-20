using System.Text.Json.Nodes;

namespace CodeFlow.Runtime;

public abstract record AgentDecision(
    AgentDecisionKind Kind,
    JsonNode? DecisionPayload = null);

public sealed record CompletedDecision(JsonNode? DecisionPayload = null)
    : AgentDecision(AgentDecisionKind.Completed, DecisionPayload);

public sealed record ApprovedDecision(JsonNode? DecisionPayload = null)
    : AgentDecision(AgentDecisionKind.Approved, DecisionPayload);

public sealed record ApprovedWithActionsDecision(
    IReadOnlyList<string> Actions,
    JsonNode? DecisionPayload = null)
    : AgentDecision(AgentDecisionKind.ApprovedWithActions, DecisionPayload);

public sealed record RejectedDecision(
    IReadOnlyList<string> Reasons,
    JsonNode? DecisionPayload = null)
    : AgentDecision(AgentDecisionKind.Rejected, DecisionPayload);

public sealed record FailedDecision(
    string Reason,
    JsonNode? DecisionPayload = null)
    : AgentDecision(AgentDecisionKind.Failed, DecisionPayload);
