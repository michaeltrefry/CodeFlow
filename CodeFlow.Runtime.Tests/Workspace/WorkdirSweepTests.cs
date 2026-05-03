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
    public void Sweep_OldTraceDirs_AreDeleted_NewTraceDirsKept_ReservedSiblingsLeftAlone()
    {
        var now = DateTimeOffset.UtcNow;
        var maxAge = TimeSpan.FromDays(14);

        var oldTraceDir = Path.Combine(root, Guid.NewGuid().ToString("N"));
        var freshTraceDir = Path.Combine(root, Guid.NewGuid().ToString("N"));
        // Reserved siblings under the unified /workspace tree: assistant per-conversation
        // root, container exec root, README. The sweep should never touch any of these
        // regardless of mtime.
        var assistantDir = Path.Combine(root, "assistant");
        var containerExecDir = Path.Combine(root, "container-workspace");
        var readme = Path.Combine(root, "README.md");

        Directory.CreateDirectory(oldTraceDir);
        File.WriteAllText(Path.Combine(oldTraceDir, "marker.txt"), "x");
        Directory.CreateDirectory(freshTraceDir);
        Directory.CreateDirectory(assistantDir);
        Directory.CreateDirectory(containerExecDir);
        File.WriteAllText(readme, "y");

        Directory.SetLastWriteTimeUtc(oldTraceDir, now.UtcDateTime.AddDays(-30));
        Directory.SetLastWriteTimeUtc(freshTraceDir, now.UtcDateTime.AddHours(-1));
        Directory.SetLastWriteTimeUtc(assistantDir, now.UtcDateTime.AddDays(-30));
        Directory.SetLastWriteTimeUtc(containerExecDir, now.UtcDateTime.AddDays(-30));
        File.SetLastWriteTimeUtc(readme, now.UtcDateTime.AddDays(-30));

        var deleted = WorkdirSweep.Sweep(root, maxAge, now);

        deleted.Should().Be(1);
        Directory.Exists(oldTraceDir).Should().BeFalse();
        Directory.Exists(freshTraceDir).Should().BeTrue();
        Directory.Exists(assistantDir).Should().BeTrue();
        Directory.Exists(containerExecDir).Should().BeTrue();
        File.Exists(readme).Should().BeTrue();
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
        var fresh1 = Path.Combine(root, Guid.NewGuid().ToString("N"));
        var fresh2 = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fresh1);
        Directory.CreateDirectory(fresh2);

        var deleted = WorkdirSweep.Sweep(root, TimeSpan.FromDays(14), now);

        deleted.Should().Be(0);
        Directory.Exists(fresh1).Should().BeTrue();
        Directory.Exists(fresh2).Should().BeTrue();
    }

    [Fact]
    public void Sweep_TtlOfOneSecond_DeletesAnyTraceWorkdirOlderThanOneSecond()
    {
        // Tight TTL acts as a "delete everything older than now-Δ" smoke test, scoped to
        // entries whose name matches the {traceId:N} shape.
        var now = DateTimeOffset.UtcNow;
        var dir = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Directory.SetLastWriteTimeUtc(dir, now.UtcDateTime.AddSeconds(-30));

        var deleted = WorkdirSweep.Sweep(root, TimeSpan.FromSeconds(1), now);

        deleted.Should().Be(1);
        Directory.Exists(dir).Should().BeFalse();
    }

    [Fact]
    public void IsTraceWorkdirName_AcceptsLowercase32Hex_RejectsEverythingElse()
    {
        WorkdirSweep.IsTraceWorkdirName(Guid.NewGuid().ToString("N")).Should().BeTrue();
        WorkdirSweep.IsTraceWorkdirName("0123456789abcdef0123456789abcdef").Should().BeTrue();

        // Uppercase hex: ToString("N") emits lowercase, so an uppercase entry isn't ours.
        WorkdirSweep.IsTraceWorkdirName("0123456789ABCDEF0123456789ABCDEF").Should().BeFalse();
        // Wrong length, hyphenated, mixed-case, reserved names.
        WorkdirSweep.IsTraceWorkdirName(Guid.NewGuid().ToString("D")).Should().BeFalse();
        WorkdirSweep.IsTraceWorkdirName("assistant").Should().BeFalse();
        WorkdirSweep.IsTraceWorkdirName("container-workspace").Should().BeFalse();
        WorkdirSweep.IsTraceWorkdirName(string.Empty).Should().BeFalse();
        WorkdirSweep.IsTraceWorkdirName("0123456789abcdef0123456789abcde").Should().BeFalse(); // 31 chars
        WorkdirSweep.IsTraceWorkdirName("0123456789abcdef0123456789abcdefa").Should().BeFalse(); // 33 chars
    }
}
