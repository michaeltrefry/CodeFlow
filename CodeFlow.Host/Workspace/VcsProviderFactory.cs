using CodeFlow.Persistence;
using CodeFlow.Runtime.Workspace;
using Microsoft.Extensions.DependencyInjection;

namespace CodeFlow.Host.Workspace;

/// <summary>
/// Builds an <see cref="IVcsProvider"/> for the currently-configured Git host on every call.
/// Resolves the scoped <see cref="IGitHostSettingsRepository"/> via <see cref="IServiceScopeFactory"/>
/// (this factory is itself a singleton), reads the mode + decrypted token, then constructs the
/// matching provider. Providers are stateless wrappers that hold the token only for the duration
/// of one call — we do not cache them across invocations.
/// </summary>
public sealed class VcsProviderFactory : IVcsProviderFactory
{
    private const string GitLabHttpClientName = "vcs.gitlab";

    private readonly IServiceScopeFactory scopeFactory;
    private readonly IHttpClientFactory httpClientFactory;

    public VcsProviderFactory(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        this.scopeFactory = scopeFactory;
        this.httpClientFactory = httpClientFactory;
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

        var token = await repository.GetDecryptedTokenAsync(cancellationToken);
        if (string.IsNullOrEmpty(token))
        {
            throw new GitHostNotConfiguredException();
        }

        return settings.Mode switch
        {
            GitHostMode.GitHub => new GitHubVcsProvider(token),
            GitHostMode.GitLab when !string.IsNullOrWhiteSpace(settings.BaseUrl) =>
                new GitLabVcsProvider(httpClientFactory.CreateClient(GitLabHttpClientName), token, settings.BaseUrl!),
            GitHostMode.GitLab => throw new GitHostNotConfiguredException(),
            _ => throw new InvalidOperationException(
                $"Unsupported git host mode '{settings.Mode}'."),
        };
    }

    public static string HttpClientName => GitLabHttpClientName;
}
