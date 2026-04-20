namespace CodeFlow.Runtime;

public sealed record InvocationResponse(
    ChatMessage Message,
    InvocationStopReason StopReason,
    TokenUsage? TokenUsage = null);
