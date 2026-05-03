namespace CodeFlow.Persistence;

public interface IWebSearchProviderSettingsRepository
{
    Task<WebSearchProviderSettings?> GetAsync(CancellationToken cancellationToken = default);

    Task<string?> GetDecryptedApiKeyAsync(CancellationToken cancellationToken = default);

    Task SetAsync(WebSearchProviderSettingsWrite write, CancellationToken cancellationToken = default);
}
