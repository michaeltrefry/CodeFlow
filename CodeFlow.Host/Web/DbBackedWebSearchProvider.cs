using CodeFlow.Persistence;
using CodeFlow.Runtime.Web;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodeFlow.Host.Web;

/// <summary>
/// Reads the admin-configured web-search adapter from the database and dispatches each
/// <see cref="IWebSearchProvider.SearchAsync"/> call to the matching backend (Brave today;
/// extensible to other providers via <see cref="WebSearchProviderKeys"/>). Falls back to the
/// "search-not-configured" structured refusal when no provider is selected, the API key is
/// missing, or the DB read fails — so agents always see a clear refusal code.
/// </summary>
public sealed class DbBackedWebSearchProvider : IWebSearchProvider, IWebSearchProviderInvalidator
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);
    private const string CacheKey = "web-search-provider-settings::active";

    private readonly IServiceScopeFactory scopeFactory;
    private readonly IMemoryCache cache;
    private readonly ILogger<DbBackedWebSearchProvider> logger;
    private readonly NullWebSearchProvider nullFallback = new();

    public DbBackedWebSearchProvider(
        IServiceScopeFactory scopeFactory,
        IMemoryCache cache,
        ILogger<DbBackedWebSearchProvider> logger)
    {
        this.scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        this.cache = cache ?? throw new ArgumentNullException(nameof(cache));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<WebSearchProviderResult> SearchAsync(
        string query,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var record = LoadRecord();
        if (record is null
            || string.Equals(record.Provider, WebSearchProviderKeys.None, StringComparison.OrdinalIgnoreCase))
        {
            return nullFallback.SearchAsync(query, maxResults, cancellationToken);
        }

        if (string.Equals(record.Provider, WebSearchProviderKeys.Brave, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(record.ApiKey))
            {
                return Task.FromResult(WebSearchProviderResult.Refused(
                    "search-not-configured",
                    "Brave Web Search is selected but no subscription token is stored. "
                    + "Set the API key on the Web Search admin page."));
            }

            var brave = new BraveWebSearchProvider(_ =>
                new BraveWebSearchProvider.BraveCredentials(record.ApiKey, record.EndpointUrl));
            return brave.SearchAsync(query, maxResults, cancellationToken);
        }

        return Task.FromResult(WebSearchProviderResult.Refused(
            "search-provider-unknown",
            $"Web search provider '{record.Provider}' is not implemented in this build."));
    }

    public void Invalidate() => cache.Remove(CacheKey);

    private CachedRecord? LoadRecord()
    {
        if (cache.TryGetValue(CacheKey, out CachedRecord? cached))
        {
            return cached;
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            var repository = scope.ServiceProvider
                .GetRequiredService<IWebSearchProviderSettingsRepository>();
            var record = repository.GetAsync().GetAwaiter().GetResult();
            var apiKey = repository.GetDecryptedApiKeyAsync().GetAwaiter().GetResult();
            var materialized = record is null
                ? null
                : new CachedRecord(record.Provider, apiKey, record.EndpointUrl);

            cache.Set(CacheKey, materialized, CacheDuration);
            return materialized;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to load web search provider settings from the database; falling back to NullWebSearchProvider.");
            return null;
        }
    }

    private sealed record CachedRecord(string Provider, string? ApiKey, string? EndpointUrl);
}

public interface IWebSearchProviderInvalidator
{
    void Invalidate();
}
