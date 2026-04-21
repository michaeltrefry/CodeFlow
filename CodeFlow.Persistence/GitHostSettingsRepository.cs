using CodeFlow.Runtime.Workspace;
using Microsoft.EntityFrameworkCore;

namespace CodeFlow.Persistence;

public sealed class GitHostSettingsRepository(CodeFlowDbContext dbContext, ISecretProtector secretProtector)
    : IGitHostSettingsRepository
{
    public async Task<GitHostSettings?> GetAsync(CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.GitHostSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);

        return entity is null ? null : Map(entity);
    }

    public async Task<string?> GetDecryptedTokenAsync(CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.GitHostSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);

        if (entity is null || entity.EncryptedToken.Length == 0)
        {
            return null;
        }

        return secretProtector.Unprotect(entity.EncryptedToken);
    }

    public async Task SetAsync(GitHostSettingsWrite write, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        ArgumentException.ThrowIfNullOrWhiteSpace(write.Token);

        if (write.Mode == GitHostMode.GitLab)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(write.BaseUrl, nameof(write.BaseUrl));
        }

        var entity = await dbContext.GitHostSettings
            .SingleOrDefaultAsync(cancellationToken);

        var now = DateTime.UtcNow;
        var encrypted = secretProtector.Protect(write.Token);

        if (entity is null)
        {
            dbContext.GitHostSettings.Add(new GitHostSettingsEntity
            {
                Key = GitHostSettingsEntity.SingletonKey,
                Mode = write.Mode,
                BaseUrl = NormalizeBaseUrl(write),
                EncryptedToken = encrypted,
                LastVerifiedAtUtc = null,
                UpdatedBy = Trim(write.UpdatedBy),
                UpdatedAtUtc = now,
            });
        }
        else
        {
            var modeChanged = entity.Mode != write.Mode;
            entity.Mode = write.Mode;
            entity.BaseUrl = NormalizeBaseUrl(write);
            entity.EncryptedToken = encrypted;
            entity.UpdatedBy = Trim(write.UpdatedBy);
            entity.UpdatedAtUtc = now;
            if (modeChanged)
            {
                entity.LastVerifiedAtUtc = null;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkVerifiedAsync(DateTime verifiedAtUtc, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.GitHostSettings
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("Git host settings have not been configured.");

        entity.LastVerifiedAtUtc = verifiedAtUtc;
        entity.UpdatedAtUtc = DateTime.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static GitHostSettings Map(GitHostSettingsEntity entity) => new(
        Mode: entity.Mode,
        BaseUrl: entity.BaseUrl,
        HasToken: entity.EncryptedToken.Length > 0,
        LastVerifiedAtUtc: entity.LastVerifiedAtUtc is DateTime lv ? DateTime.SpecifyKind(lv, DateTimeKind.Utc) : null,
        UpdatedBy: entity.UpdatedBy,
        UpdatedAtUtc: DateTime.SpecifyKind(entity.UpdatedAtUtc, DateTimeKind.Utc));

    private static string? NormalizeBaseUrl(GitHostSettingsWrite write)
    {
        if (write.Mode != GitHostMode.GitLab)
        {
            return null;
        }

        var trimmed = write.BaseUrl?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.TrimEnd('/');
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
