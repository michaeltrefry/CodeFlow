using CodeFlow.Persistence;
using CodeFlow.Runtime.Workspace;
using Microsoft.Extensions.DependencyInjection;

namespace CodeFlow.Host.Workspace;

public sealed class VcsProviderFactory : IVcsProviderFactory
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly IReadOnlyDictionary<GitHostMode, IVcsProvider> providers;

    public VcsProviderFactory(
        IServiceScopeFactory scopeFactory,
        IEnumerable<IVcsProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(providers);

        this.scopeFactory = scopeFactory;
        this.providers = providers.ToDictionary(p => p.Mode);
    }

    public async Task<IVcsProvider> CreateAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IGitHostSettingsRepository>();

        var settings = await repository.GetAsync(cancellationToken);
        if (settings is null || !settings.HasToken)
        {
            throw new GitHostNotConfiguredException();
        }

        if (!providers.TryGetValue(settings.Mode, out var provider))
        {
            throw new InvalidOperationException(
                $"No IVcsProvider is registered for mode '{settings.Mode}'.");
        }

        return provider;
    }
}
