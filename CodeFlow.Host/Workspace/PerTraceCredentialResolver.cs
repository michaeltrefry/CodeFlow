using CodeFlow.Persistence;
using CodeFlow.Runtime.Workspace;
using Microsoft.Extensions.DependencyInjection;

namespace CodeFlow.Host.Workspace;

/// <summary>
/// <see cref="IPerTraceCredentialResolver"/> backed by <see cref="IGitHostSettingsRepository"/>.
/// Mirror of <see cref="VcsProviderFactory"/>: this resolver is itself a singleton that opens a
/// scope per call so it can read the (currently single) configured git host and decrypted token.
///
/// CodeFlow today supports one git host per installation, so the result is at most one
/// <see cref="HostCredential"/> entry — but the API stays per-host so the multi-host future
/// (different tokens for github.com vs. github.example.internal) doesn't need a new contract.
/// When the configured host doesn't match any of the trace's declared repos, the result is
/// empty and the trace runs without git auth.
/// </summary>
public sealed class PerTraceCredentialResolver : IPerTraceCredentialResolver
{
    private readonly IServiceScopeFactory scopeFactory;

    public PerTraceCredentialResolver(IServiceScopeFactory scopeFactory)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        this.scopeFactory = scopeFactory;
    }

    public async Task<IReadOnlyList<HostCredential>> ResolveAsync(
        IReadOnlyList<string> repoUrls,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repoUrls);
        if (repoUrls.Count == 0)
        {
            return Array.Empty<HostCredential>();
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IGitHostSettingsRepository>();

        var settings = await repository.GetAsync(cancellationToken);
        if (settings is null || !settings.HasToken)
        {
            return Array.Empty<HostCredential>();
        }

        var token = await repository.GetDecryptedTokenAsync(cancellationToken);
        if (string.IsNullOrEmpty(token))
        {
            return Array.Empty<HostCredential>();
        }

        var hostsInScope = ExtractDistinctHosts(repoUrls);
        if (hostsInScope.Count == 0)
        {
            return Array.Empty<HostCredential>();
        }

        var (configuredHost, username) = ResolveConfiguredHost(settings);
        var matched = new List<HostCredential>();
        foreach (var host in hostsInScope)
        {
            if (string.Equals(host, configuredHost, StringComparison.OrdinalIgnoreCase))
            {
                matched.Add(new HostCredential(host, username, token));
            }
        }

        return matched;
    }

    private static IReadOnlyList<string> ExtractDistinctHosts(IReadOnlyList<string> repoUrls)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var url in repoUrls)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }
            try
            {
                var repo = RepoReference.Parse(url);
                if (string.Equals(repo.Host, "local", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                seen.Add(repo.Host);
            }
            catch (ArgumentException)
            {
                // Defence-in-depth: gate already validated the URL shape upstream.
            }
        }
        return seen.ToArray();
    }

    private static (string Host, string Username) ResolveConfiguredHost(GitHostSettings settings) =>
        settings.Mode switch
        {
            GitHostMode.GitHub => ("github.com", "x-access-token"),
            GitHostMode.GitLab => (ResolveGitLabHost(settings.BaseUrl), "oauth2"),
            _ => throw new InvalidOperationException($"Unsupported git host mode '{settings.Mode}'."),
        };

    private static string ResolveGitLabHost(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return "gitlab.com";
        }
        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            return uri.Host.ToLowerInvariant();
        }
        return "gitlab.com";
    }
}
