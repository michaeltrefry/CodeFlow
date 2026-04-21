using CodeFlow.Persistence;
using CodeFlow.Runtime.Workspace;
using Microsoft.Extensions.DependencyInjection;

namespace CodeFlow.Host.Workspace;

public sealed class RepoUrlHostGuard : IRepoUrlHostGuard
{
    private readonly IServiceScopeFactory scopeFactory;

    public RepoUrlHostGuard(IServiceScopeFactory scopeFactory)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        this.scopeFactory = scopeFactory;
    }

    public async Task AssertAllowedAsync(RepoReference repo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);

        await using var scope = scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IGitHostSettingsRepository>();

        var settings = await repository.GetAsync(cancellationToken);
        RepoUrlHostPolicy.AssertMatches(settings, repo);
    }
}
