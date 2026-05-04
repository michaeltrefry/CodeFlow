using CodeFlow.Runtime.Workspace;
using FluentAssertions;

namespace CodeFlow.Runtime.Tests.Workspace;

public sealed class RepoUrlHostPolicyTests
{
    [Fact]
    public void AssertMatches_allows_any_host_when_settings_not_configured()
    {
        var repo = RepoReference.Parse("https://github.com/a/b");

        var act = () => RepoUrlHostPolicy.AssertMatches(null, repo);

        act.Should().NotThrow();
    }

    [Fact]
    public void AssertMatches_allows_any_host_when_token_not_set()
    {
        var repo = RepoReference.Parse("https://example.com/a/b");
        var settings = new GitHostSettings(
            Mode: GitHostMode.GitHub,
            BaseUrl: null,
            HasToken: false,
            LastVerifiedAtUtc: null,
            UpdatedBy: null,
            UpdatedAtUtc: DateTime.UtcNow);

        var act = () => RepoUrlHostPolicy.AssertMatches(settings, repo);

        act.Should().NotThrow();
    }

    [Fact]
    public void AssertMatches_allows_github_host_in_github_mode()
    {
        var repo = RepoReference.Parse("https://github.com/acme/widget");
        var settings = Configured(GitHostMode.GitHub);

        var act = () => RepoUrlHostPolicy.AssertMatches(settings, repo);

        act.Should().NotThrow();
    }

    [Fact]
    public void AssertMatches_rejects_non_github_host_in_github_mode()
    {
        var repo = RepoReference.Parse("https://gitlab.example.com/a/b");
        var settings = Configured(GitHostMode.GitHub);

        var act = () => RepoUrlHostPolicy.AssertMatches(settings, repo);

        act.Should().Throw<RepoUrlHostMismatchException>()
            .WithMessage("*github.com*");
    }

    [Fact]
    public void AssertMatches_allows_configured_gitlab_host()
    {
        var repo = RepoReference.Parse("https://gitlab.tcdevelops.com/team/project");
        var settings = Configured(GitHostMode.GitLab, "https://gitlab.tcdevelops.com");

        var act = () => RepoUrlHostPolicy.AssertMatches(settings, repo);

        act.Should().NotThrow();
    }

    [Fact]
    public void AssertMatches_rejects_different_gitlab_host_when_gitlab_configured()
    {
        var repo = RepoReference.Parse("https://gitlab.other.com/team/project");
        var settings = Configured(GitHostMode.GitLab, "https://gitlab.tcdevelops.com");

        var act = () => RepoUrlHostPolicy.AssertMatches(settings, repo);

        act.Should().Throw<RepoUrlHostMismatchException>()
            .WithMessage("*gitlab.tcdevelops.com*");
    }

    [Fact]
    public void AssertMatches_rejects_github_url_when_gitlab_configured()
    {
        var repo = RepoReference.Parse("https://github.com/a/b");
        var settings = Configured(GitHostMode.GitLab, "https://gitlab.tcdevelops.com");

        var act = () => RepoUrlHostPolicy.AssertMatches(settings, repo);

        act.Should().Throw<RepoUrlHostMismatchException>();
    }

    [Fact]
    public void AssertMatches_throws_invalidoperation_when_gitlab_baseUrl_missing()
    {
        var repo = RepoReference.Parse("https://gitlab.example.com/a/b");
        var settings = Configured(GitHostMode.GitLab, baseUrl: null);

        var act = () => RepoUrlHostPolicy.AssertMatches(settings, repo);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task WorkspaceService_propagates_RepoUrlHostMismatchException()
    {
        using var fakeCli = new ThrowingGitCli();
        var options = new WorkspaceOptions
        {
            Root = Path.Combine(Path.GetTempPath(), "codeflow-c24-" + Guid.NewGuid().ToString("N")),
        };
        Directory.CreateDirectory(options.Root);
        try
        {
            var guard = new ThrowingHostGuard();
            var service = new WorkspaceService(options, fakeCli, guard);

            var act = () => service.OpenAsync(Guid.NewGuid(), "https://github.com/a/b");

            await act.Should().ThrowAsync<RepoUrlHostMismatchException>();
        }
        finally
        {
            Directory.Delete(options.Root, recursive: true);
        }
    }

    private static GitHostSettings Configured(GitHostMode mode, string? baseUrl = null) =>
        new(Mode: mode,
            BaseUrl: baseUrl,
            HasToken: true,
            LastVerifiedAtUtc: null,
            UpdatedBy: "admin",
            UpdatedAtUtc: DateTime.UtcNow);

    private sealed class ThrowingHostGuard : IRepoUrlHostGuard
    {
        public Task AssertAllowedAsync(RepoReference repo, CancellationToken cancellationToken = default)
            => throw new RepoUrlHostMismatchException("test mismatch");
    }

    private sealed class ThrowingGitCli : IGitCli, IDisposable
    {
        public void Dispose() { }

        private static Exception NotCalled() => new InvalidOperationException("GitCli should not be invoked when guard rejects URL.");

        public Task CloneMirrorAsync(string originUrl, string destinationMirrorPath, CancellationToken cancellationToken = default) => throw NotCalled();
        public Task<GitCloneResult> CloneAsync(string originUrl, string destinationPath, string? branch = null, int? depth = null, IReadOnlyDictionary<string, string>? environmentVariables = null, CancellationToken cancellationToken = default) => throw NotCalled();
        public Task FetchAsync(string mirrorPath, CancellationToken cancellationToken = default) => throw NotCalled();
        public Task WorktreeAddAsync(string mirrorPath, string worktreePath, string branchName, string? startPoint = null, CancellationToken cancellationToken = default) => throw NotCalled();
        public Task WorktreeRemoveAsync(string mirrorPath, string worktreePath, bool force = false, CancellationToken cancellationToken = default) => throw NotCalled();
        public Task CreateBranchAsync(string worktreePath, string branchName, string? startPoint = null, CancellationToken cancellationToken = default) => throw NotCalled();
        public Task CheckoutAsync(string worktreePath, string branchOrRef, CancellationToken cancellationToken = default) => throw NotCalled();
        public Task AddAsync(string worktreePath, IReadOnlyList<string>? paths = null, CancellationToken cancellationToken = default) => throw NotCalled();
        public Task<bool> CommitAsync(string worktreePath, string message, CancellationToken cancellationToken = default) => throw NotCalled();
        public Task PushAsync(string worktreePath, string? remote = null, string? branch = null, CancellationToken cancellationToken = default) => throw NotCalled();
        public Task<string> RevParseAsync(string worktreePath, string rev, CancellationToken cancellationToken = default) => throw NotCalled();
        public Task<string> GetSymbolicHeadAsync(string gitDirectory, CancellationToken cancellationToken = default) => throw NotCalled();
        public Task<IReadOnlyList<string>> LsFilesAsync(string worktreePath, CancellationToken cancellationToken = default) => throw NotCalled();
        public Task<IReadOnlyList<GitStatusEntry>> StatusAsync(string worktreePath, CancellationToken cancellationToken = default) => throw NotCalled();
    }
}
