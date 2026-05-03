using Microsoft.EntityFrameworkCore;

namespace CodeFlow.Persistence;

public sealed class WebSearchProviderSettingsRepository(
    CodeFlowDbContext dbContext,
    ISecretProtector secretProtector)
    : IWebSearchProviderSettingsRepository
{
    public async Task<WebSearchProviderSettings?> GetAsync(CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.WebSearchProviders
            .AsNoTracking()
            .SingleOrDefaultAsync(
                e => e.Id == WebSearchProviderSettingsEntity.SingletonId,
                cancellationToken);

        return entity is null ? null : Map(entity);
    }

    public async Task<string?> GetDecryptedApiKeyAsync(CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.WebSearchProviders
            .AsNoTracking()
            .SingleOrDefaultAsync(
                e => e.Id == WebSearchProviderSettingsEntity.SingletonId,
                cancellationToken);

        if (entity?.EncryptedApiKey is null || entity.EncryptedApiKey.Length == 0)
        {
            return null;
        }

        return secretProtector.Unprotect(entity.EncryptedApiKey);
    }

    public async Task SetAsync(
        WebSearchProviderSettingsWrite write,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        ArgumentNullException.ThrowIfNull(write.Token);
        if (!WebSearchProviderKeys.IsKnown(write.Provider))
        {
            throw new ArgumentException(
                $"Unknown web search provider '{write.Provider}'.",
                nameof(write));
        }

        var canonicalProvider = WebSearchProviderKeys.Canonicalize(write.Provider);
        var entity = await dbContext.WebSearchProviders
            .SingleOrDefaultAsync(
                e => e.Id == WebSearchProviderSettingsEntity.SingletonId,
                cancellationToken);

        var now = DateTime.UtcNow;
        var endpointUrl = NormalizeString(write.EndpointUrl);

        if (entity is null)
        {
            byte[]? encrypted = null;
            if (write.Token.Action == WebSearchProviderTokenAction.Replace)
            {
                if (string.IsNullOrWhiteSpace(write.Token.Value))
                {
                    throw new ArgumentException(
                        "Token value is required when action is Replace.",
                        nameof(write));
                }
                encrypted = secretProtector.Protect(write.Token.Value);
            }

            dbContext.WebSearchProviders.Add(new WebSearchProviderSettingsEntity
            {
                Id = WebSearchProviderSettingsEntity.SingletonId,
                Provider = canonicalProvider,
                EncryptedApiKey = encrypted,
                EndpointUrl = endpointUrl,
                UpdatedBy = NormalizeString(write.UpdatedBy),
                UpdatedAtUtc = now,
            });
        }
        else
        {
            entity.Provider = canonicalProvider;
            entity.EndpointUrl = endpointUrl;
            entity.UpdatedBy = NormalizeString(write.UpdatedBy);
            entity.UpdatedAtUtc = now;

            switch (write.Token.Action)
            {
                case WebSearchProviderTokenAction.Replace:
                    if (string.IsNullOrWhiteSpace(write.Token.Value))
                    {
                        throw new ArgumentException(
                            "Token value is required when action is Replace.",
                            nameof(write));
                    }
                    entity.EncryptedApiKey = secretProtector.Protect(write.Token.Value);
                    break;
                case WebSearchProviderTokenAction.Clear:
                    entity.EncryptedApiKey = null;
                    break;
                case WebSearchProviderTokenAction.Preserve:
                default:
                    break;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static WebSearchProviderSettings Map(WebSearchProviderSettingsEntity entity) => new(
        Provider: entity.Provider,
        HasApiKey: entity.EncryptedApiKey is { Length: > 0 },
        EndpointUrl: entity.EndpointUrl,
        UpdatedBy: entity.UpdatedBy,
        UpdatedAtUtc: DateTime.SpecifyKind(entity.UpdatedAtUtc, DateTimeKind.Utc));

    private static string? NormalizeString(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
