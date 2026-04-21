namespace CodeFlow.Runtime;

public sealed record ResolvedAgentTools(
    IReadOnlyCollection<string> AllowedToolNames,
    IReadOnlyList<McpToolDefinition> McpTools,
    bool EnableHostTools,
    IReadOnlyList<ResolvedSkill> GrantedSkills)
{
    public ResolvedAgentTools(
        IReadOnlyCollection<string> AllowedToolNames,
        IReadOnlyList<McpToolDefinition> McpTools,
        bool EnableHostTools)
        : this(AllowedToolNames, McpTools, EnableHostTools, Array.Empty<ResolvedSkill>())
    {
    }

    public static ResolvedAgentTools Empty { get; } = new(
        Array.Empty<string>(),
        Array.Empty<McpToolDefinition>(),
        false,
        Array.Empty<ResolvedSkill>());
}
