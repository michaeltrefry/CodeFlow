using System.Text.Json;

namespace CodeFlow.Runtime;

public sealed record AgentInvocationResult(
    string Output,
    AgentDecision Decision,
    IReadOnlyList<ChatMessage> Transcript,
    TokenUsage? TokenUsage = null,
    int ToolCallsExecuted = 0,
    IReadOnlyDictionary<string, JsonElement>? ContextUpdates = null,
    IReadOnlyDictionary<string, JsonElement>? WorkflowUpdates = null);
