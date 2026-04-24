using CodeFlow.Persistence;
using CodeFlow.Runtime;
using CodeFlow.Runtime.Anthropic;
using CodeFlow.Runtime.LMStudio;
using CodeFlow.Runtime.OpenAI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodeFlow.Host;

/// <summary>
/// Resolves per-provider options by overlaying the DB-stored admin settings (runtime-editable) on
/// top of the startup appsettings defaults. Cached with a short TTL so operators can rotate
/// credentials without a restart; the API invalidates the cache on save so the next invocation
/// sees fresh state immediately.
/// </summary>
public sealed class DbBackedLlmProviderConfigResolver : ILlmProviderConfigResolver, ILlmProviderConfigInvalidator
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory scopeFactory;
    private readonly IMemoryCache cache;
    private readonly ILogger<DbBackedLlmProviderConfigResolver> logger;
    private readonly OpenAIModelClientOptions openAiDefaults;
    private readonly AnthropicModelClientOptions anthropicDefaults;
    private readonly LMStudioModelClientOptions lmStudioDefaults;

    public DbBackedLlmProviderConfigResolver(
        IServiceScopeFactory scopeFactory,
        IMemoryCache cache,
        OpenAIModelClientOptions openAiDefaults,
        AnthropicModelClientOptions anthropicDefaults,
        LMStudioModelClientOptions lmStudioDefaults,
        ILogger<DbBackedLlmProviderConfigResolver> logger)
    {
        this.scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        this.cache = cache ?? throw new ArgumentNullException(nameof(cache));
        this.openAiDefaults = openAiDefaults ?? throw new ArgumentNullException(nameof(openAiDefaults));
        this.anthropicDefaults = anthropicDefaults ?? throw new ArgumentNullException(nameof(anthropicDefaults));
        this.lmStudioDefaults = lmStudioDefaults ?? throw new ArgumentNullException(nameof(lmStudioDefaults));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public OpenAIModelClientOptions ResolveOpenAI()
    {
        var record = LoadRecord(LlmProviderKeys.OpenAi);
        return openAiDefaults with
        {
            ApiKey = record?.ApiKey ?? openAiDefaults.ApiKey,
            ResponsesEndpoint = ParseUriOrDefault(record?.EndpointUrl, openAiDefaults.ResponsesEndpoint),
        };
    }

    public AnthropicModelClientOptions ResolveAnthropic()
    {
        var record = LoadRecord(LlmProviderKeys.Anthropic);
        return anthropicDefaults with
        {
            ApiKey = record?.ApiKey ?? anthropicDefaults.ApiKey,
            MessagesEndpoint = ParseUriOrDefault(record?.EndpointUrl, anthropicDefaults.MessagesEndpoint),
            ApiVersion = !string.IsNullOrWhiteSpace(record?.ApiVersion)
                ? record!.ApiVersion!
                : anthropicDefaults.ApiVersion,
        };
    }

    public LMStudioModelClientOptions ResolveLMStudio()
    {
        var record = LoadRecord(LlmProviderKeys.LmStudio);
        return lmStudioDefaults with
        {
            ApiKey = record?.ApiKey ?? lmStudioDefaults.ApiKey,
            ResponsesEndpoint = ParseUriOrDefault(record?.EndpointUrl, lmStudioDefaults.ResponsesEndpoint),
        };
    }

    public IReadOnlyList<string> ResolveConfiguredModels(string provider)
    {
        if (!LlmProviderKeys.IsKnown(provider))
        {
            return Array.Empty<string>();
        }
        var record = LoadRecord(LlmProviderKeys.Canonicalize(provider));
        return record?.Models ?? Array.Empty<string>();
    }

    public void Invalidate(string provider)
    {
        if (!LlmProviderKeys.IsKnown(provider))
        {
            return;
        }
        cache.Remove(CacheKey(LlmProviderKeys.Canonicalize(provider)));
    }

    private CachedProviderRecord? LoadRecord(string provider)
    {
        if (cache.TryGetValue(CacheKey(provider), out CachedProviderRecord? cached))
        {
            return cached;
        }

        // Synchronous lookup on the agent invocation hot path is unfortunate but unavoidable —
        // the model client interface is synchronous-to-the-options. The 30s cache turns this
        // into effectively one DB read per provider per 30 seconds per instance.
        try
        {
            using var scope = scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<ILlmProviderSettingsRepository>();
            var record = repository.GetAsync(provider).GetAwaiter().GetResult();
            var apiKey = repository.GetDecryptedApiKeyAsync(provider).GetAwaiter().GetResult();
            var materialized = record is null
                ? (apiKey is null ? null : new CachedProviderRecord(apiKey, null, null, Array.Empty<string>()))
                : new CachedProviderRecord(apiKey, record.EndpointUrl, record.ApiVersion, record.Models);

            cache.Set(CacheKey(provider), materialized, CacheDuration);
            return materialized;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load LLM provider '{Provider}' settings from the database; falling back to appsettings.", provider);
            return null;
        }
    }

    private static string CacheKey(string provider) => $"llm-provider-settings::{provider}";

    private static Uri ParseUriOrDefault(string? value, Uri fallback)
    {
        if (!string.IsNullOrWhiteSpace(value) && Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return uri;
        }
        return fallback;
    }

    private sealed record CachedProviderRecord(
        string? ApiKey,
        string? EndpointUrl,
        string? ApiVersion,
        IReadOnlyList<string> Models);
}

public interface ILlmProviderConfigInvalidator
{
    void Invalidate(string provider);
}
