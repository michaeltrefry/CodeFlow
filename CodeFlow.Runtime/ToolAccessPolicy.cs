namespace CodeFlow.Runtime;

public sealed record ToolAccessPolicy(
    IReadOnlyCollection<string>? AllowedToolNames = null,
    IReadOnlyDictionary<ToolCategory, int>? CategoryToolLimits = null)
{
    public static ToolAccessPolicy AllowAll { get; } = new();

    public bool AllowsTool(string toolName)
    {
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
