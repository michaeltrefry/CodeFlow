using Microsoft.EntityFrameworkCore;

namespace CodeFlow.Persistence.Notifications;

public sealed class NotificationProviderConfigRepository(
    CodeFlowDbContext dbContext,
    ISecretProtector secretProtector)
    : INotificationProviderConfigRepository
{
    public async Task<IReadOnlyList<NotificationProviderConfig>> ListAsync(
        bool includeArchived = false,
        CancellationToken cancellationToken = default)
    {
        var query = dbContext.NotificationProviders.AsNoTracking();
        if (!includeArchived)
        {
            query = query.Where(p => !p.IsArchived);
        }

        var entities = await query
            .OrderBy(p => p.Id)
            .ToListAsync(cancellationToken);

        return entities.Select(Map).ToArray();
    }

    public async Task<NotificationProviderConfig?> GetAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        var entity = await dbContext.NotificationProviders
            .AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == providerId, cancellationToken);

        return entity is null ? null : Map(entity);
    }

    public async Task<NotificationProviderConfigWithCredential?> GetWithDecryptedCredentialAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        var entity = await dbContext.NotificationProviders
            .AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == providerId, cancellationToken);

        if (entity is null)
        {
            return null;
        }

        string? plaintext = null;
        if (entity.EncryptedCredential is { Length: > 0 })
        {
            plaintext = secretProtector.Unprotect(entity.EncryptedCredential);
        }

        return new NotificationProviderConfigWithCredential(Map(entity), plaintext);
    }

    public async Task UpsertAsync(NotificationProviderUpsert upsert, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upsert);
        ArgumentNullException.ThrowIfNull(upsert.Credential);
        ArgumentException.ThrowIfNullOrWhiteSpace(upsert.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(upsert.DisplayName);

        var now = DateTime.UtcNow;
        var entity = await dbContext.NotificationProviders
            .SingleOrDefaultAsync(p => p.Id == upsert.Id, cancellationToken);

        if (entity is null)
        {
            byte[]? encrypted = null;
            switch (upsert.Credential.Action)
            {
                case NotificationProviderCredentialAction.Replace:
                    if (string.IsNullOrEmpty(upsert.Credential.Plaintext))
                    {
                        throw new ArgumentException(
                            "Credential plaintext is required when action is Replace.",
                            nameof(upsert));
                    }
                    encrypted = secretProtector.Protect(upsert.Credential.Plaintext);
                    break;
                case NotificationProviderCredentialAction.Clear:
                case NotificationProviderCredentialAction.Preserve:
                default:
                    break;
            }

            dbContext.NotificationProviders.Add(new NotificationProviderEntity
            {
                Id = upsert.Id,
                DisplayName = upsert.DisplayName,
                Channel = upsert.Channel,
                EndpointUrl = NormalizeString(upsert.EndpointUrl),
                FromAddress = NormalizeString(upsert.FromAddress),
                EncryptedCredential = encrypted,
                AdditionalConfigJson = NormalizeString(upsert.AdditionalConfigJson),
                Enabled = upsert.Enabled,
                IsArchived = false,
                CreatedAtUtc = now,
                CreatedBy = NormalizeString(upsert.UpdatedBy),
                UpdatedAtUtc = now,
                UpdatedBy = NormalizeString(upsert.UpdatedBy),
            });
        }
        else
        {
            entity.DisplayName = upsert.DisplayName;
            entity.Channel = upsert.Channel;
            entity.EndpointUrl = NormalizeString(upsert.EndpointUrl);
            entity.FromAddress = NormalizeString(upsert.FromAddress);
            entity.AdditionalConfigJson = NormalizeString(upsert.AdditionalConfigJson);
            entity.Enabled = upsert.Enabled;
            entity.IsArchived = false;
            entity.UpdatedAtUtc = now;
            entity.UpdatedBy = NormalizeString(upsert.UpdatedBy);

            switch (upsert.Credential.Action)
            {
                case NotificationProviderCredentialAction.Replace:
                    if (string.IsNullOrEmpty(upsert.Credential.Plaintext))
                    {
                        throw new ArgumentException(
                            "Credential plaintext is required when action is Replace.",
                            nameof(upsert));
                    }
                    entity.EncryptedCredential = secretProtector.Protect(upsert.Credential.Plaintext);
                    break;
                case NotificationProviderCredentialAction.Clear:
                    entity.EncryptedCredential = null;
                    break;
                case NotificationProviderCredentialAction.Preserve:
                default:
                    break;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task ArchiveAsync(string providerId, string? archivedBy, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        var entity = await dbContext.NotificationProviders
            .SingleOrDefaultAsync(p => p.Id == providerId, cancellationToken);

        if (entity is null)
        {
            return;
        }

        entity.IsArchived = true;
        entity.Enabled = false;
        entity.UpdatedAtUtc = DateTime.UtcNow;
        entity.UpdatedBy = NormalizeString(archivedBy);

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static NotificationProviderConfig Map(NotificationProviderEntity entity) => new(
        Id: entity.Id,
        DisplayName: entity.DisplayName,
        Channel: entity.Channel,
        EndpointUrl: entity.EndpointUrl,
        FromAddress: entity.FromAddress,
        HasCredential: entity.EncryptedCredential is { Length: > 0 },
        AdditionalConfigJson: entity.AdditionalConfigJson,
        Enabled: entity.Enabled,
        IsArchived: entity.IsArchived,
        CreatedAtUtc: DateTime.SpecifyKind(entity.CreatedAtUtc, DateTimeKind.Utc),
        CreatedBy: entity.CreatedBy,
        UpdatedAtUtc: DateTime.SpecifyKind(entity.UpdatedAtUtc, DateTimeKind.Utc),
        UpdatedBy: entity.UpdatedBy);

    private static string? NormalizeString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        return value.Trim();
    }
}
