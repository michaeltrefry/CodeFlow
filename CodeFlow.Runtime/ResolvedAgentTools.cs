namespace CodeFlow.Runtime;

public sealed record ResolvedAgentTools(
    IReadOnlyCollection<string> AllowedToolNames,
    IReadOnlyList<McpToolDefinition> McpTools,
    bool EnableHostTools)
{
    public static ResolvedAgentTools Empty { get; } = new(
        Array.Empty<string>(),
        Array.Empty<McpToolDefinition>(),
        false);
}
