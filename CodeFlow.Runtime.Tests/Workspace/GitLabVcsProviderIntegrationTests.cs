using CodeFlow.Runtime.Workspace;
using FluentAssertions;

namespace CodeFlow.Runtime.Tests.Workspace;

[Trait("Category", "VcsIntegration")]
public sealed class GitLabVcsProviderIntegrationTests
{
    [Fact]
    public async Task GetRepoMetadataAsync_against_real_gitlab_returns_default_branch()
    {
        var env = VcsIntegrationEnv.GitLab();
        if (env is null)
        {
            return;
        }

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var tokenProvider = new StaticTokenProvider(env.Value.token);
        var reader = new StaticSettingsReader(new GitHostSettings(
            Mode: GitHostMode.GitLab,
            BaseUrl: env.Value.baseUrl,
            HasToken: true,
            LastVerifiedAtUtc: null,
            UpdatedBy: null,
            UpdatedAtUtc: DateTime.UtcNow));

        var provider = new GitLabVcsProvider(httpClient, tokenProvider, reader);

        var metadata = await provider.GetRepoMetadataAsync(env.Value.owner, env.Value.name);

        metadata.DefaultBranch.Should().NotBeNullOrWhiteSpace();
        metadata.CloneUrl.Should().Contain(new Uri(env.Value.baseUrl).Host);
    }

    private sealed class StaticTokenProvider : IGitHostTokenProvider
    {
        private readonly string token;
        public StaticTokenProvider(string token) { this.token = token; }
        public Task<GitHostTokenLease> AcquireAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new GitHostTokenLease(token));
    }

    private sealed class StaticSettingsReader : IGitHostSettingsReader
    {
        private readonly GitHostSettings settings;
        public StaticSettingsReader(GitHostSettings settings) { this.settings = settings; }
        public Task<GitHostSettings?> GetAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<GitHostSettings?>(settings);
    }
}
