using CodeFlow.Persistence;
using CodeFlow.Runtime.Workspace;
using Microsoft.Extensions.DependencyInjection;

namespace CodeFlow.Host.Workspace;

public sealed class GitHostTokenProvider : IGitHostTokenProvider
{
    private readonly IServiceScopeFactory scopeFactory;

    public GitHostTokenProvider(IServiceScopeFactory scopeFactory)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        this.scopeFactory = scopeFactory;
    }

    public async Task<GitHostTokenLease> AcquireAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IGitHostSettingsRepository>();

        var token = await repository.GetDecryptedTokenAsync(cancellationToken);
        if (string.IsNullOrEmpty(token))
        {
            throw new GitHostNotConfiguredException();
        }

        return new GitHostTokenLease(token);
    }
}
