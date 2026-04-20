namespace CodeFlow.Runtime;

public sealed record ToolResult(
    string CallId,
    string Content,
    bool IsError = false);
