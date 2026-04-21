using CodeFlow.Runtime.Workspace;
using FluentAssertions;

namespace CodeFlow.Runtime.Tests.Workspace;

[Trait("Category", "VcsIntegration")]
public sealed class GitHubVcsProviderIntegrationTests
{
    [Fact]
    public async Task GetRepoMetadataAsync_against_real_github_returns_default_branch()
    {
        var env = VcsIntegrationEnv.GitHub();
        if (env is null)
        {
            return;
        }

        var tokenProvider = new StaticTokenProvider(env.Value.token);
        var provider = new GitHubVcsProvider(tokenProvider);

        var metadata = await provider.GetRepoMetadataAsync(env.Value.owner, env.Value.name);

        metadata.DefaultBranch.Should().NotBeNullOrWhiteSpace();
        metadata.CloneUrl.Should().StartWith("https://github.com/");
    }

    private sealed class StaticTokenProvider : IGitHostTokenProvider
    {
        private readonly string token;
        public StaticTokenProvider(string token) { this.token = token; }
        public Task<GitHostTokenLease> AcquireAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new GitHostTokenLease(token));
    }
}
