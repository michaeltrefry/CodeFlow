namespace CodeFlow.Runtime;

public sealed record AgentInvocationResult(
    string Output,
    AgentDecision Decision,
    IReadOnlyList<ChatMessage> Transcript,
    TokenUsage? TokenUsage = null,
    int ToolCallsExecuted = 0);
