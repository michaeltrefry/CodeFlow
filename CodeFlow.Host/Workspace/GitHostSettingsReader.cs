using CodeFlow.Persistence;
using CodeFlow.Runtime.Workspace;
using Microsoft.Extensions.DependencyInjection;

namespace CodeFlow.Host.Workspace;

public sealed class GitHostSettingsReader : IGitHostSettingsReader
{
    private readonly IServiceScopeFactory scopeFactory;

    public GitHostSettingsReader(IServiceScopeFactory scopeFactory)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        this.scopeFactory = scopeFactory;
    }

    public async Task<GitHostSettings?> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IGitHostSettingsRepository>();
        return await repository.GetAsync(cancellationToken);
    }
}
