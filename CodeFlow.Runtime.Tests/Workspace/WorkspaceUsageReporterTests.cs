using CodeFlow.Runtime.Workspace;
using FluentAssertions;

namespace CodeFlow.Runtime.Tests.Workspace;

public sealed class WorkspaceUsageReporterTests : IDisposable
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
    public async Task GetUsageAsync_reports_root_and_cache_bytes_and_worktree_count()
    {
        var root = NewTempDir("codeflow-usage-root");
        var cachePath = Path.Combine(root, WorkspaceOptions.CacheDirectoryName);
        var workPath = Path.Combine(root, WorkspaceOptions.WorkDirectoryName);
        Directory.CreateDirectory(cachePath);
        Directory.CreateDirectory(workPath);

        // Cache: one mirror with a 512-byte file.
        var mirrorDir = Path.Combine(cachePath, "github.com", "acme", "repo.git");
        Directory.CreateDirectory(mirrorDir);
        File.WriteAllBytes(Path.Combine(mirrorDir, "packed-refs"), new byte[512]);

        // Work: two correlations, one repo each.
        var corrA = Path.Combine(workPath, "aaa");
        var corrB = Path.Combine(workPath, "bbb");
        Directory.CreateDirectory(Path.Combine(corrA, "acme-one"));
        Directory.CreateDirectory(Path.Combine(corrB, "acme-two"));
        File.WriteAllBytes(Path.Combine(corrA, "acme-one", "a.txt"), new byte[128]);

        var options = new WorkspaceOptions
        {
            Root = root,
            DiskUsageWarnBytes = 1_000_000,
            DiskUsageCacheDuration = TimeSpan.FromSeconds(60),
        };
        var reporter = new WorkspaceUsageReporter(options, () => DateTimeOffset.UtcNow);

        var usage = await reporter.GetUsageAsync();

        usage.CacheBytes.Should().BeGreaterThanOrEqualTo(512);
        usage.RootBytes.Should().BeGreaterThanOrEqualTo(512 + 128);
        usage.WorktreeCount.Should().Be(2);
        usage.AboveWarn.Should().BeFalse();
        usage.WarnThresholdBytes.Should().Be(1_000_000);
    }

    [Fact]
    public async Task GetUsageAsync_caches_result_for_configured_duration()
    {
        var root = NewTempDir("codeflow-usage-cache-root");
        Directory.CreateDirectory(Path.Combine(root, WorkspaceOptions.CacheDirectoryName));
        Directory.CreateDirectory(Path.Combine(root, WorkspaceOptions.WorkDirectoryName));
        File.WriteAllBytes(Path.Combine(root, "seed.bin"), new byte[100]);

        var clock = new DateTimeOffset(2026, 4, 21, 12, 0, 0, TimeSpan.Zero);
        var options = new WorkspaceOptions
        {
            Root = root,
            DiskUsageCacheDuration = TimeSpan.FromSeconds(60),
        };
        var reporter = new WorkspaceUsageReporter(options, () => clock);

        var first = await reporter.GetUsageAsync();

        // Write more data — the cached result should not reflect it.
        File.WriteAllBytes(Path.Combine(root, "more.bin"), new byte[1_000_000]);

        clock = clock.AddSeconds(30);
        var cached = await reporter.GetUsageAsync();
        cached.RootBytes.Should().Be(first.RootBytes);

        // Jump past the cache window.
        clock = clock.AddSeconds(40);
        var fresh = await reporter.GetUsageAsync();
        fresh.RootBytes.Should().BeGreaterThan(first.RootBytes);
    }

    [Fact]
    public async Task GetUsageAsync_flags_above_warn_when_root_bytes_exceed_threshold()
    {
        var root = NewTempDir("codeflow-usage-warn-root");
        File.WriteAllBytes(Path.Combine(root, "big.bin"), new byte[2048]);

        var options = new WorkspaceOptions
        {
            Root = root,
            DiskUsageWarnBytes = 1024,
            DiskUsageCacheDuration = TimeSpan.FromSeconds(60),
        };
        var reporter = new WorkspaceUsageReporter(options, () => DateTimeOffset.UtcNow);

        var usage = await reporter.GetUsageAsync();

        usage.AboveWarn.Should().BeTrue();
    }
}
