using CodeFlow.Runtime;
using System.Text.Json;

namespace CodeFlow.Persistence;

public sealed record WorkflowEdge(
    string FromAgentKey,
    AgentDecisionKind Decision,
    JsonElement? Discriminator,
    string ToAgentKey,
    bool RotatesRound,
    int SortOrder);
