using CodeFlow.Runtime.Workspace;
using FluentAssertions;

namespace CodeFlow.Runtime.Tests.Workspace;

public sealed class WorkdirSweepTests : IDisposable
{
    private readonly string root;

    public WorkdirSweepTests()
    {
        root = Path.Combine(Path.GetTempPath(), $"codeflow-sweep-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Sweep_OldEntries_AreDeleted_NewEntriesKept()
    {
        var now = DateTimeOffset.UtcNow;
        var maxAge = TimeSpan.FromDays(14);

        var oldDir = Path.Combine(root, Guid.NewGuid().ToString("N"));
        var oldFile = Path.Combine(root, "stray.txt");
        var freshDir = Path.Combine(root, Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(oldDir);
        File.WriteAllText(Path.Combine(oldDir, "marker.txt"), "x");
        File.WriteAllText(oldFile, "y");
        Directory.CreateDirectory(freshDir);

        Directory.SetLastWriteTimeUtc(oldDir, now.UtcDateTime.AddDays(-30));
        File.SetLastWriteTimeUtc(oldFile, now.UtcDateTime.AddDays(-30));
        Directory.SetLastWriteTimeUtc(freshDir, now.UtcDateTime.AddHours(-1));

        var deleted = WorkdirSweep.Sweep(root, maxAge, now);

        deleted.Should().Be(2);
        Directory.Exists(oldDir).Should().BeFalse();
        File.Exists(oldFile).Should().BeFalse();
        Directory.Exists(freshDir).Should().BeTrue();
    }

    [Fact]
    public void Sweep_NoConfig_ReturnsZeroQuietly()
    {
        WorkdirSweep.Sweep(null, TimeSpan.FromDays(1), DateTimeOffset.UtcNow).Should().Be(0);
        WorkdirSweep.Sweep("", TimeSpan.FromDays(1), DateTimeOffset.UtcNow).Should().Be(0);
    }

    [Fact]
    public void Sweep_NonexistentRoot_ReturnsZeroQuietly()
    {
        var ghost = Path.Combine(Path.GetTempPath(), $"codeflow-sweep-ghost-{Guid.NewGuid():N}");
        WorkdirSweep.Sweep(ghost, TimeSpan.FromDays(1), DateTimeOffset.UtcNow).Should().Be(0);
    }

    [Fact]
    public void Sweep_EmptyRoot_ReturnsZero()
    {
        WorkdirSweep.Sweep(root, TimeSpan.FromDays(1), DateTimeOffset.UtcNow).Should().Be(0);
    }

    [Fact]
    public void Sweep_AllFresh_KeepsAll()
    {
        var now = DateTimeOffset.UtcNow;
        var fresh1 = Path.Combine(root, "a");
        var fresh2 = Path.Combine(root, "b");
        Directory.CreateDirectory(fresh1);
        Directory.CreateDirectory(fresh2);

        var deleted = WorkdirSweep.Sweep(root, TimeSpan.FromDays(14), now);

        deleted.Should().Be(0);
        Directory.Exists(fresh1).Should().BeTrue();
        Directory.Exists(fresh2).Should().BeTrue();
    }

    [Fact]
    public void Sweep_TtlOfOneSecond_DeletesAnythingOlderThanOneSecond()
    {
        // Tight TTL acts as a "delete everything older than now-Δ" smoke test.
        var now = DateTimeOffset.UtcNow;
        var dir = Path.Combine(root, "old");
        Directory.CreateDirectory(dir);
        Directory.SetLastWriteTimeUtc(dir, now.UtcDateTime.AddSeconds(-30));

        var deleted = WorkdirSweep.Sweep(root, TimeSpan.FromSeconds(1), now);

        deleted.Should().Be(1);
        Directory.Exists(dir).Should().BeFalse();
    }
}
