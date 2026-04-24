using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace CodeFlow.Persistence;

public sealed class LlmProviderSettingsRepository(CodeFlowDbContext dbContext, ISecretProtector secretProtector)
    : ILlmProviderSettingsRepository
{
    public async Task<IReadOnlyList<LlmProviderSettings>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var entities = await dbContext.LlmProviders
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return entities.Select(Map).ToArray();
    }

    public async Task<LlmProviderSettings?> GetAsync(string provider, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        var normalized = LlmProviderKeys.Canonicalize(provider);

        var entity = await dbContext.LlmProviders
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.Provider == normalized, cancellationToken);

        return entity is null ? null : Map(entity);
    }

    public async Task<string?> GetDecryptedApiKeyAsync(string provider, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        var normalized = LlmProviderKeys.Canonicalize(provider);

        var entity = await dbContext.LlmProviders
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.Provider == normalized, cancellationToken);

        if (entity?.EncryptedApiKey is null || entity.EncryptedApiKey.Length == 0)
        {
            return null;
        }

        return secretProtector.Unprotect(entity.EncryptedApiKey);
    }

    public async Task SetAsync(LlmProviderSettingsWrite write, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        ArgumentNullException.ThrowIfNull(write.Token);
        if (!LlmProviderKeys.IsKnown(write.Provider))
        {
            throw new ArgumentException($"Unknown LLM provider '{write.Provider}'.", nameof(write));
        }

        var normalized = LlmProviderKeys.Canonicalize(write.Provider);
        var entity = await dbContext.LlmProviders
            .SingleOrDefaultAsync(e => e.Provider == normalized, cancellationToken);

        var now = DateTime.UtcNow;
        var endpointUrl = NormalizeString(write.EndpointUrl);
        var apiVersion = NormalizeString(write.ApiVersion);
        var modelsJson = SerializeModels(write.Models);

        if (entity is null)
        {
            if (write.Token.Action == LlmProviderTokenAction.Preserve)
            {
                // Nothing previously stored, Preserve is a no-op on key; only save if there's
                // actually non-token content to write.
                if (endpointUrl is null && apiVersion is null && modelsJson is null)
                {
                    return;
                }
            }

            byte[]? encrypted = null;
            if (write.Token.Action == LlmProviderTokenAction.Replace)
            {
                if (string.IsNullOrWhiteSpace(write.Token.Value))
                {
                    throw new ArgumentException("Token value is required when action is Replace.", nameof(write));
                }
                encrypted = secretProtector.Protect(write.Token.Value);
            }

            dbContext.LlmProviders.Add(new LlmProviderSettingsEntity
            {
                Provider = normalized,
                EncryptedApiKey = encrypted,
                EndpointUrl = endpointUrl,
                ApiVersion = apiVersion,
                ModelsJson = modelsJson,
                UpdatedBy = NormalizeString(write.UpdatedBy),
                UpdatedAtUtc = now,
            });
        }
        else
        {
            entity.EndpointUrl = endpointUrl;
            entity.ApiVersion = apiVersion;
            entity.ModelsJson = modelsJson;
            entity.UpdatedBy = NormalizeString(write.UpdatedBy);
            entity.UpdatedAtUtc = now;

            switch (write.Token.Action)
            {
                case LlmProviderTokenAction.Replace:
                    if (string.IsNullOrWhiteSpace(write.Token.Value))
                    {
                        throw new ArgumentException("Token value is required when action is Replace.", nameof(write));
                    }
                    entity.EncryptedApiKey = secretProtector.Protect(write.Token.Value);
                    break;
                case LlmProviderTokenAction.Clear:
                    entity.EncryptedApiKey = null;
                    break;
                case LlmProviderTokenAction.Preserve:
                default:
                    break;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static LlmProviderSettings Map(LlmProviderSettingsEntity entity) => new(
        Provider: entity.Provider,
        HasApiKey: entity.EncryptedApiKey is { Length: > 0 },
        EndpointUrl: entity.EndpointUrl,
        ApiVersion: entity.ApiVersion,
        Models: DeserializeModels(entity.ModelsJson),
        UpdatedBy: entity.UpdatedBy,
        UpdatedAtUtc: DateTime.SpecifyKind(entity.UpdatedAtUtc, DateTimeKind.Utc));

    private static IReadOnlyList<string> DeserializeModels(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<string>();
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<string[]>(json);
            return parsed is null ? Array.Empty<string>() : parsed;
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    private static string? SerializeModels(IReadOnlyList<string>? models)
    {
        if (models is null)
        {
            return null;
        }

        var cleaned = models
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return JsonSerializer.Serialize(cleaned);
    }

    private static string? NormalizeString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        return value.Trim();
    }
}
