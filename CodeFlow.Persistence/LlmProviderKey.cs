namespace CodeFlow.Persistence;

public static class LlmProviderKeys
{
    public const string OpenAi = "openai";
    public const string Anthropic = "anthropic";
    public const string LmStudio = "lmstudio";

    public static readonly IReadOnlyList<string> All = new[] { OpenAi, Anthropic, LmStudio };

    public static bool IsKnown(string? provider) =>
        !string.IsNullOrWhiteSpace(provider)
        && All.Any(p => string.Equals(p, provider, StringComparison.OrdinalIgnoreCase));

    public static string Canonicalize(string provider) =>
        All.First(p => string.Equals(p, provider, StringComparison.OrdinalIgnoreCase));
}
