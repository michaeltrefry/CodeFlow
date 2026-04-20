namespace CodeFlow.Runtime;

public sealed record InvocationLoopResult(
    string Output,
    AgentDecision Decision,
    IReadOnlyList<ChatMessage> Transcript,
    TokenUsage? TokenUsage = null,
    int ToolCallsExecuted = 0);
