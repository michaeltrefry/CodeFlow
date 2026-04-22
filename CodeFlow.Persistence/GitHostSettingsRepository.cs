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
        ArgumentNullException.ThrowIfNull(write.Token);

        if (write.Mode == GitHostMode.GitLab)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(write.BaseUrl, nameof(write.BaseUrl));
        }

        var entity = await dbContext.GitHostSettings
            .SingleOrDefaultAsync(cancellationToken);

        var now = DateTime.UtcNow;

        if (entity is null)
        {
            if (write.Token.Action == GitHostTokenAction.Preserve
                || string.IsNullOrWhiteSpace(write.Token.Value))
            {
                throw new InvalidOperationException(
                    "A token must be supplied when creating git host settings; Preserve is only valid when settings already exist.");
            }

            dbContext.GitHostSettings.Add(new GitHostSettingsEntity
            {
                Key = GitHostSettingsEntity.SingletonKey,
                Mode = write.Mode,
                BaseUrl = NormalizeBaseUrl(write),
                EncryptedToken = secretProtector.Protect(write.Token.Value!),
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
            entity.UpdatedBy = Trim(write.UpdatedBy);
            entity.UpdatedAtUtc = now;

            if (write.Token.Action == GitHostTokenAction.Replace)
            {
                if (string.IsNullOrWhiteSpace(write.Token.Value))
                {
                    throw new ArgumentException(
                        "Token value is required when action is Replace.",
                        nameof(write));
                }
                entity.EncryptedToken = secretProtector.Protect(write.Token.Value!);
                // Token rotated — last verification is no longer authoritative.
                entity.LastVerifiedAtUtc = null;
            }
            else if (modeChanged)
            {
                // Mode change invalidates the existing verification but the token (for the new
                // host) is unchanged; operator must re-verify.
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
