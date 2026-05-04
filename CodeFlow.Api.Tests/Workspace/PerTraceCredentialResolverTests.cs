using CodeFlow.Host.Workspace;
using CodeFlow.Persistence;
using CodeFlow.Runtime.Workspace;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CodeFlow.Api.Tests.Workspace;

public sealed class PerTraceCredentialResolverTests
{
    [Fact]
    public async Task ResolveAsync_NoConfiguredHost_ReturnsEmpty()
    {
        var resolver = BuildResolver(settings: null, decryptedToken: null);

        var creds = await resolver.ResolveAsync(new[] { "https://github.com/owner/repo" });

        creds.Should().BeEmpty("no token configured ⇒ no auth ⇒ git ops will fail loudly at the helper rather than fall through to a default credential");
    }

    [Fact]
    public async Task ResolveAsync_HasTokenButTokenStringIsEmpty_ReturnsEmpty()
    {
        var settings = MakeSettings(GitHostMode.GitHub, baseUrl: null, hasToken: true);
        var resolver = BuildResolver(settings, decryptedToken: "");

        var creds = await resolver.ResolveAsync(new[] { "https://github.com/owner/repo" });

        creds.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_GitHubHost_DerivesEntryForGitHubReposOnly()
    {
        var settings = MakeSettings(GitHostMode.GitHub, baseUrl: null, hasToken: true);
        var resolver = BuildResolver(settings, decryptedToken: "ghp_secret");

        var creds = await resolver.ResolveAsync(new[]
        {
            "https://github.com/owner/repo",
            "https://gitlab.com/group/proj",
            "https://github.com/other/proj",
        });

        creds.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new HostCredential("github.com", "x-access-token", "ghp_secret"));
    }

    [Fact]
    public async Task ResolveAsync_GitLabBaseUrl_TakesHostFromBaseUrl()
    {
        var settings = MakeSettings(GitHostMode.GitLab, baseUrl: "https://git.example.internal", hasToken: true);
        var resolver = BuildResolver(settings, decryptedToken: "glpat_secret");

        var creds = await resolver.ResolveAsync(new[]
        {
            "https://git.example.internal/group/proj",
            "https://github.com/owner/repo",
        });

        creds.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new HostCredential("git.example.internal", "oauth2", "glpat_secret"));
    }

    [Fact]
    public async Task ResolveAsync_NoMatchingRepoForConfiguredHost_ReturnsEmpty()
    {
        var settings = MakeSettings(GitHostMode.GitHub, baseUrl: null, hasToken: true);
        var resolver = BuildResolver(settings, decryptedToken: "ghp_secret");

        var creds = await resolver.ResolveAsync(new[]
        {
            "https://gitlab.com/group/proj",
            "https://bitbucket.org/owner/repo",
        });

        creds.Should().BeEmpty("the trace declares only repos on hosts we don't have a token for");
    }

    [Fact]
    public async Task ResolveAsync_DistinctHostsAreCollapsed_NoDuplicateEntryWhenMultipleReposShareHost()
    {
        var settings = MakeSettings(GitHostMode.GitHub, baseUrl: null, hasToken: true);
        var resolver = BuildResolver(settings, decryptedToken: "ghp_secret");

        var creds = await resolver.ResolveAsync(new[]
        {
            "https://github.com/owner1/repo1",
            "https://github.com/owner2/repo2",
            "https://github.com/owner3/repo3",
        });

        creds.Should().ContainSingle()
            .Which.Host.Should().Be("github.com");
    }

    [Fact]
    public async Task ResolveAsync_LocalFileUrls_AreSkipped()
    {
        var settings = MakeSettings(GitHostMode.GitHub, baseUrl: null, hasToken: true);
        var resolver = BuildResolver(settings, decryptedToken: "ghp_secret");

        var creds = await resolver.ResolveAsync(new[] { "file:///tmp/local-fixture.git" });

        creds.Should().BeEmpty("local file remotes don't need credentials and shouldn't pretend to have any");
    }

    [Fact]
    public async Task ResolveAsync_EmptyInputs_ShortCircuitsBeforeTouchingTheRepository()
    {
        var resolver = BuildResolver(settings: null, decryptedToken: null);

        var creds = await resolver.ResolveAsync(Array.Empty<string>());

        creds.Should().BeEmpty();
    }

    private static PerTraceCredentialResolver BuildResolver(GitHostSettings? settings, string? decryptedToken)
    {
        var services = new ServiceCollection();
        services.AddScoped<IGitHostSettingsRepository>(_ => new StubRepository(settings, decryptedToken));
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        return new PerTraceCredentialResolver(scopeFactory);
    }

    private static GitHostSettings MakeSettings(GitHostMode mode, string? baseUrl, bool hasToken) =>
        new(
            Mode: mode,
            BaseUrl: baseUrl,
            HasToken: hasToken,
            LastVerifiedAtUtc: null,
            UpdatedBy: null,
            UpdatedAtUtc: DateTime.UtcNow,
            WorkingDirectoryMaxAgeDays: null);

    private sealed class StubRepository : IGitHostSettingsRepository
    {
        private readonly GitHostSettings? settings;
        private readonly string? token;

        public StubRepository(GitHostSettings? settings, string? token)
        {
            this.settings = settings;
            this.token = token;
        }

        public Task<GitHostSettings?> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(settings);

        public Task<string?> GetDecryptedTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(token);

        public Task SetAsync(GitHostSettingsWrite write, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException("not used in resolver tests");

        public Task MarkVerifiedAsync(DateTime verifiedAtUtc, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException("not used in resolver tests");
    }
}
