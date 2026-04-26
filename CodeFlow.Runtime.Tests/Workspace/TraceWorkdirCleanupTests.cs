using CodeFlow.Runtime.Workspace;
using FluentAssertions;

namespace CodeFlow.Runtime.Tests.Workspace;

public sealed class TraceWorkdirCleanupTests
{
    [Fact]
    public void TryRemove_ExistingDir_DeletesAndReturnsTrue()
    {
        var root = NewRoot();
        var traceId = Guid.NewGuid();
        var workDir = Path.Combine(root, traceId.ToString("N"));
        Directory.CreateDirectory(workDir);
        File.WriteAllText(Path.Combine(workDir, "marker.txt"), "x");

        try
        {
            var removed = TraceWorkdirCleanup.TryRemove(root, traceId);

            removed.Should().BeTrue();
            Directory.Exists(workDir).Should().BeFalse();
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void TryRemove_NoConfig_ReturnsFalseQuietly()
    {
        TraceWorkdirCleanup.TryRemove(null, Guid.NewGuid()).Should().BeFalse();
        TraceWorkdirCleanup.TryRemove("", Guid.NewGuid()).Should().BeFalse();
        TraceWorkdirCleanup.TryRemove("   ", Guid.NewGuid()).Should().BeFalse();
    }

    [Fact]
    public void TryRemove_MissingDir_ReturnsFalseQuietly()
    {
        var root = NewRoot();
        Directory.CreateDirectory(root);
        try
        {
            TraceWorkdirCleanup.TryRemove(root, Guid.NewGuid()).Should().BeFalse();
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), $"codeflow-cleanup-test-{Guid.NewGuid():N}");

    private static void Cleanup(string root)
    {
        try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
    }
}
