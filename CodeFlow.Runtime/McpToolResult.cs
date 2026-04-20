namespace CodeFlow.Runtime;

public sealed record McpToolResult(
    string Content,
    bool IsError = false);
