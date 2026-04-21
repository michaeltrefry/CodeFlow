using CodeFlow.Runtime.Workspace;

namespace CodeFlow.Persistence;

public interface IGitHostSettingsRepository
{
    Task<GitHostSettings?> GetAsync(CancellationToken cancellationToken = default);

    Task<string?> GetDecryptedTokenAsync(CancellationToken cancellationToken = default);

    Task SetAsync(GitHostSettingsWrite write, CancellationToken cancellationToken = default);

    Task MarkVerifiedAsync(DateTime verifiedAtUtc, CancellationToken cancellationToken = default);
}
