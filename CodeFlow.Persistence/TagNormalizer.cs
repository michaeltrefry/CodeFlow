namespace CodeFlow.Persistence;

public static class TagNormalizer
{
    public const int MaxTags = 5;

    public static IReadOnlyList<string> Normalize(IReadOnlyList<string>? tags)
    {
        if (tags is null || tags.Count == 0)
        {
            return Array.Empty<string>();
        }

        return tags
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxTags)
            .ToArray();
    }
}
