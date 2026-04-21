using CodeFlow.Runtime.Workspace;
using FluentAssertions;

namespace CodeFlow.Runtime.Tests.Workspace;

public sealed class WorktreeSweeperTests : IDisposable
{
    private readonly List<string> cleanupDirs = [];

    public void Dispose()
    {
        foreach (var dir in cleanupDirs)
        {
            GitTestRepo.SafeDelete(dir);
        }
    }

    private string NewTempDir(string prefix)
    {
        var dir = GitTestRepo.CreateTempDirectory(prefix);
        cleanupDirs.Add(dir);
        return dir;
    }

    [Fact]
    public async Task SweepAsync_removes_correlation_dirs_older_than_ttl()
    {
        var root = NewTempDir("codeflow-sweep-root");
        var workPath = Path.Combine(root, WorkspaceOptions.WorkDirectoryName);
        var cachePath = Path.Combine(root, WorkspaceOptions.CacheDirectoryName);
        Directory.CreateDirectory(workPath);
        Directory.CreateDirectory(cachePath);

        var staleDir = Path.Combine(workPath, "stale-correlation");
        var freshDir = Path.Combine(workPath, "fresh-correlation");
        Directory.CreateDirectory(staleDir);
        Directory.CreateDirectory(freshDir);

        var fixedNow = new DateTimeOffset(2026, 4, 21, 12, 0, 0, TimeSpan.Zero);
        Directory.SetLastWriteTimeUtc(staleDir, fixedNow.UtcDateTime - TimeSpan.FromDays(2));
        Directory.SetLastWriteTimeUtc(freshDir, fixedNow.UtcDateTime - TimeSpan.FromMinutes(5));

        var options = new WorkspaceOptions
        {
            Root = root,
            WorktreeTtl = TimeSpan.FromHours(24),
        };
        var sweeper = new WorktreeSweeper(options, new NoopGitCli(), () => fixedNow);

        var report = await sweeper.SweepAsync();

        Directory.Exists(staleDir).Should().BeFalse();
        Directory.Exists(freshDir).Should().BeTrue();
        report.CorrelationsPurged.Should().Be(1);
    }

    [Fact]
    public async Task SweepAsync_returns_zero_when_work_path_missing()
    {
        var root = NewTempDir("codeflow-sweep-root");
        var options = new WorkspaceOptions { Root = root, WorktreeTtl = TimeSpan.FromHours(24) };
        var sweeper = new WorktreeSweeper(options, new NoopGitCli(), () => DateTimeOffset.UtcNow);

        var report = await sweeper.SweepAsync();

        report.CorrelationsPurged.Should().Be(0);
        report.MirrorsPruned.Should().Be(0);
    }

    [Fact]
    public async Task SweepAsync_prunes_each_mirror_once_when_cache_has_bare_repos()
    {
        var root = NewTempDir("codeflow-sweep-root");
        var workPath = Path.Combine(root, WorkspaceOptions.WorkDirectoryName);
        var cachePath = Path.Combine(root, WorkspaceOptions.CacheDirectoryName);
        Directory.CreateDirectory(workPath);
        Directory.CreateDirectory(cachePath);

        var mirrorA = Path.Combine(cachePath, "github.com", "acme", "a.git");
        var mirrorB = Path.Combine(cachePath, "github.com", "acme", "b.git");
        Directory.CreateDirectory(mirrorA);
        Directory.CreateDirectory(mirrorB);
        File.WriteAllText(Path.Combine(mirrorA, "HEAD"), "ref: refs/heads/main\n");
        File.WriteAllText(Path.Combine(mirrorB, "HEAD"), "ref: refs/heads/main\n");

        var options = new WorkspaceOptions
        {
            Root = root,
            WorktreeTtl = TimeSpan.FromHours(24),
        };
        var recorder = new RecordingGitCli();
        var sweeper = new WorktreeSweeper(options, recorder, () => DateTimeOffset.UtcNow);

        var report = await sweeper.SweepAsync();

        report.MirrorsPruned.Should().Be(2);
        recorder.PruneCalls.Should().HaveCount(2)
            .And.Contain(mirrorA)
            .And.Contain(mirrorB);
    }

    private sealed class NoopGitCli : IGitCli
    {
        public Task CloneMirrorAsync(string originUrl, string destinationMirrorPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task FetchAsync(string mirrorPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task WorktreeAddAsync(string mirrorPath, string worktreePath, string branchName, string? startPoint = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task WorktreeRemoveAsync(string mirrorPath, string worktreePath, bool force = false, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task WorktreePruneAsync(string mirrorPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CreateBranchAsync(string worktreePath, string branchName, string? startPoint = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CheckoutAsync(string worktreePath, string branchOrRef, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AddAsync(string worktreePath, IReadOnlyList<string>? paths = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> CommitAsync(string worktreePath, string message, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task PushAsync(string worktreePath, string? remote = null, string? branch = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PushWithBearerAsync(string worktreePath, string bearerToken, string? remote = null, string? branch = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string> RevParseAsync(string worktreePath, string rev, CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);
        public Task<string> GetSymbolicHeadAsync(string gitDirectory, CancellationToken cancellationToken = default) => Task.FromResult("main");
        public Task<IReadOnlyList<string>> LsFilesAsync(string worktreePath, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<GitStatusEntry>> StatusAsync(string worktreePath, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GitStatusEntry>>([]);
    }

    private sealed class RecordingGitCli : NoopGitCliBase
    {
        public List<string> PruneCalls { get; } = [];

        public override Task WorktreePruneAsync(string mirrorPath, CancellationToken cancellationToken = default)
        {
            PruneCalls.Add(mirrorPath);
            return Task.CompletedTask;
        }
    }

    private class NoopGitCliBase : IGitCli
    {
        public virtual Task CloneMirrorAsync(string originUrl, string destinationMirrorPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public virtual Task FetchAsync(string mirrorPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public virtual Task WorktreeAddAsync(string mirrorPath, string worktreePath, string branchName, string? startPoint = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public virtual Task WorktreeRemoveAsync(string mirrorPath, string worktreePath, bool force = false, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public virtual Task WorktreePruneAsync(string mirrorPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public virtual Task CreateBranchAsync(string worktreePath, string branchName, string? startPoint = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public virtual Task CheckoutAsync(string worktreePath, string branchOrRef, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public virtual Task AddAsync(string worktreePath, IReadOnlyList<string>? paths = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public virtual Task<bool> CommitAsync(string worktreePath, string message, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public virtual Task PushAsync(string worktreePath, string? remote = null, string? branch = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public virtual Task PushWithBearerAsync(string worktreePath, string bearerToken, string? remote = null, string? branch = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public virtual Task<string> RevParseAsync(string worktreePath, string rev, CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);
        public virtual Task<string> GetSymbolicHeadAsync(string gitDirectory, CancellationToken cancellationToken = default) => Task.FromResult("main");
        public virtual Task<IReadOnlyList<string>> LsFilesAsync(string worktreePath, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public virtual Task<IReadOnlyList<GitStatusEntry>> StatusAsync(string worktreePath, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GitStatusEntry>>([]);
    }
}
