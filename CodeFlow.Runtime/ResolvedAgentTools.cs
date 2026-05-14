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

    /// <summary>
    /// Epic 993 / NO-6: union this tool set with <paramref name="other"/>. Additive only — the
    /// result is the superset of both sides: unioned <see cref="AllowedToolNames"/>, unioned
    /// <see cref="McpTools"/> (deduped by <see cref="McpToolDefinition.FullName"/>), unioned
    /// <see cref="GrantedSkills"/> (deduped by name), and <see cref="EnableHostTools"/> true when
    /// either side enables host tools. Nothing is ever removed.
    /// </summary>
    public ResolvedAgentTools Merge(ResolvedAgentTools other)
    {
        ArgumentNullException.ThrowIfNull(other);

        var allowedNames = new HashSet<string>(AllowedToolNames, StringComparer.OrdinalIgnoreCase);
        foreach (var name in other.AllowedToolNames)
        {
            allowedNames.Add(name);
        }

        var mcpByName = new Dictionary<string, McpToolDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in McpTools)
        {
            mcpByName[tool.FullName] = tool;
        }
        foreach (var tool in other.McpTools)
        {
            mcpByName[tool.FullName] = tool;
        }

        var skillsByName = new Dictionary<string, ResolvedSkill>(StringComparer.OrdinalIgnoreCase);
        foreach (var skill in GrantedSkills)
        {
            skillsByName[skill.Name] = skill;
        }
        foreach (var skill in other.GrantedSkills)
        {
            skillsByName[skill.Name] = skill;
        }

        return new ResolvedAgentTools(
            allowedNames,
            mcpByName.Values.ToList(),
            EnableHostTools || other.EnableHostTools,
            skillsByName.Values.ToList());
    }
}
