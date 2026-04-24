namespace CodeFlow.Persistence;

public interface ILlmProviderSettingsRepository
{
    Task<IReadOnlyList<LlmProviderSettings>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<LlmProviderSettings?> GetAsync(string provider, CancellationToken cancellationToken = default);

    Task<string?> GetDecryptedApiKeyAsync(string provider, CancellationToken cancellationToken = default);

    Task SetAsync(LlmProviderSettingsWrite write, CancellationToken cancellationToken = default);
}
