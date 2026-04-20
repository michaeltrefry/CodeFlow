namespace CodeFlow.Runtime;

public sealed class UnknownToolException(string toolName)
    : InvalidOperationException($"Tool '{toolName}' is not available for this invocation.")
{
    public string ToolName { get; } = toolName;
}
