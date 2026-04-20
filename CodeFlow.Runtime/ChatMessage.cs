namespace CodeFlow.Runtime;

public sealed record ChatMessage(
    ChatMessageRole Role,
    string Content,
    IReadOnlyList<ToolCall>? ToolCalls = null,
    string? ToolCallId = null,
    bool IsError = false);
