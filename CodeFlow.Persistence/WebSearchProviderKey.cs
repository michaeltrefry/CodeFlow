namespace CodeFlow.Persistence;

/// <summary>
/// Provider keys for the singleton web-search adapter row. <see cref="None"/> means the
/// admin has explicitly chosen no provider; the runtime treats it the same as a missing row
/// (returns the "search-not-configured" structured refusal).
/// </summary>
public static class WebSearchProviderKeys
{
    public const string None = "none";
    public const string Brave = "brave";

    public static readonly IReadOnlyList<string> All = new[] { None, Brave };

    public static bool IsKnown(string? provider) =>
        !string.IsNullOrWhiteSpace(provider)
        && All.Any(p => string.Equals(p, provider, StringComparison.OrdinalIgnoreCase));

    public static string Canonicalize(string provider) =>
        All.First(p => string.Equals(p, provider, StringComparison.OrdinalIgnoreCase));
}
