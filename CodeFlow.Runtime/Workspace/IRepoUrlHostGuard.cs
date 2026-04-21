namespace CodeFlow.Runtime.Workspace;

public interface IRepoUrlHostGuard
{
    Task AssertAllowedAsync(RepoReference repo, CancellationToken cancellationToken = default);
}

public sealed class PermissiveRepoUrlHostGuard : IRepoUrlHostGuard
{
    public Task AssertAllowedAsync(RepoReference repo, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

public static class RepoUrlHostPolicy
{
    public static void AssertMatches(GitHostSettings? settings, RepoReference repo)
    {
        ArgumentNullException.ThrowIfNull(repo);

        if (settings is null || !settings.HasToken)
        {
            return;
        }

        var expectedHost = ResolveExpectedHost(settings);
        if (!string.Equals(expectedHost, repo.Host, StringComparison.OrdinalIgnoreCase))
        {
            throw new RepoUrlHostMismatchException(
                $"Repo host '{repo.Host}' does not match the configured git host '{expectedHost}'. " +
                $"This CodeFlow instance is configured for {settings.Mode}.");
        }
    }

    private static string ResolveExpectedHost(GitHostSettings settings)
    {
        if (settings.Mode == GitHostMode.GitHub)
        {
            return "github.com";
        }

        if (!string.IsNullOrWhiteSpace(settings.BaseUrl)
            && Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out var uri))
        {
            return uri.Host.ToLowerInvariant();
        }

        throw new InvalidOperationException("GitLab mode is configured but BaseUrl is missing or invalid.");
    }
}
