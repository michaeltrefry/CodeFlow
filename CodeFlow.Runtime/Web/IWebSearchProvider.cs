namespace CodeFlow.Runtime.Web;

/// <summary>
/// Adapter for the actual search backend (DDG/Brave/Perplexity/etc.). v1 leaves this as an
/// extension point — operators register their preferred provider when wiring DI; the default
/// <see cref="NullWebSearchProvider"/> emits a structured refusal so agents see a clear
/// "search-not-configured" message instead of ambient failures.
/// </summary>
public interface IWebSearchProvider
{
    Task<WebSearchProviderResult> SearchAsync(
        string query,
        int maxResults,
        CancellationToken cancellationToken);
}

public sealed record WebSearchProviderResult(
    bool Ok,
    IReadOnlyList<WebSearchHit> Hits,
    string? RefusalCode = null,
    string? RefusalReason = null)
{
    public static WebSearchProviderResult Success(IReadOnlyList<WebSearchHit> hits) =>
        new(true, hits);

    public static WebSearchProviderResult Refused(string code, string reason) =>
        new(false, Array.Empty<WebSearchHit>(), code, reason);
}

public sealed record WebSearchHit(string Title, string Url, string? Snippet = null);

public sealed class NullWebSearchProvider : IWebSearchProvider
{
    public Task<WebSearchProviderResult> SearchAsync(
        string query,
        int maxResults,
        CancellationToken cancellationToken) =>
        Task.FromResult(WebSearchProviderResult.Refused(
            "search-not-configured",
            "web_search is enabled in policy but no IWebSearchProvider is registered; "
            + "wire a real provider (DDG/Brave/Perplexity API) at host startup or remove "
            + "web_search from the agent's role grants."));
}
