namespace CodeFlow.Runtime;

public sealed record ToolAccessPolicy(
    IReadOnlyCollection<string>? AllowedToolNames = null,
    IReadOnlyDictionary<ToolCategory, int>? CategoryToolLimits = null,
    bool DenyAll = false)
{
    public static ToolAccessPolicy AllowAll { get; } = new();

    /// <summary>
    /// Denies every tool unconditionally. Used for the assistant's demo mode (HAA-6) so anonymous
    /// homepage chats run system-prompt-only, with no access to registry or trace tools.
    /// </summary>
    public static ToolAccessPolicy NoTools { get; } = new(DenyAll: true);

    public bool AllowsTool(string toolName)
    {
        if (DenyAll)
        {
            return false;
        }

        if (AllowedToolNames is null || AllowedToolNames.Count == 0)
        {
            return true;
        }

        return AllowedToolNames.Any(allowedToolName =>
            string.Equals(allowedToolName, toolName, StringComparison.OrdinalIgnoreCase));
    }

    public int GetCategoryLimit(ToolCategory category)
    {
        if (CategoryToolLimits is null || !CategoryToolLimits.TryGetValue(category, out var limit))
        {
            return int.MaxValue;
        }

        return Math.Max(0, limit);
    }
}
