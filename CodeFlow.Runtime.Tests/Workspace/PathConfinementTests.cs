using CodeFlow.Runtime.Workspace;
using FluentAssertions;

namespace CodeFlow.Runtime.Tests.Workspace;

public sealed class PathConfinementTests : IDisposable
{
    private readonly string root;

    public PathConfinementTests()
    {
        root = Path.Combine(Path.GetTempPath(), "codeflow-pathconf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void Resolve_ShouldReturnPathUnderRoot_ForSimpleRelative()
    {
        var resolved = PathConfinement.Resolve(root, "src/main.cs");

        resolved.Should().StartWith(Path.GetFullPath(root));
        Path.GetFileName(resolved).Should().Be("main.cs");
    }

    [Fact]
    public void Resolve_ShouldReject_WhenPathEscapesRootViaDotDot()
    {
        var act = () => PathConfinement.Resolve(root, "../../etc/passwd");

        act.Should().Throw<PathConfinementException>()
            .WithMessage("*outside*");
    }

    [Fact]
    public void Resolve_ShouldReject_WhenPathEscapesNestedViaDotDot()
    {
        var act = () => PathConfinement.Resolve(root, "src/../../outside");

        act.Should().Throw<PathConfinementException>();
    }

    [Fact]
    public void Resolve_ShouldReject_WhenPathIsAbsolute()
    {
        var absolute = OperatingSystem.IsWindows() ? "C:\\Windows\\System32" : "/etc/passwd";

        var act = () => PathConfinement.Resolve(root, absolute);

        act.Should().Throw<PathConfinementException>()
            .WithMessage("*Absolute*");
    }

    [Fact]
    public void Resolve_ShouldReject_WhenSymlinkTargetEscapesRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var outsideDir = Path.Combine(Path.GetTempPath(), "codeflow-pathconf-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideDir);

        try
        {
            var linkPath = Path.Combine(root, "escape-link");
            File.CreateSymbolicLink(linkPath, outsideDir);

            var act = () => PathConfinement.Resolve(root, "escape-link");

            act.Should().Throw<PathConfinementException>();
        }
        finally
        {
            Directory.Delete(outsideDir, recursive: true);
        }
    }

    [Fact]
    public void Resolve_ShouldAllow_WhenSymlinkTargetIsInsideRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var insideTarget = Path.Combine(root, "inside-target");
        Directory.CreateDirectory(insideTarget);

        var linkPath = Path.Combine(root, "inside-link");
        Directory.CreateSymbolicLink(linkPath, insideTarget);

        var resolved = PathConfinement.Resolve(root, "inside-link");

        resolved.Should().StartWith(Path.GetFullPath(root));
    }

    [Fact]
    public void Resolve_ShouldHandle_TrailingSeparatorInRoot()
    {
        var rootWithTrailer = root + Path.DirectorySeparatorChar;

        var resolved = PathConfinement.Resolve(rootWithTrailer, "file.txt");

        resolved.Should().StartWith(Path.GetFullPath(root));
    }

    [Fact]
    public void Resolve_ShouldHandle_ForwardSlashOnWindows()
    {
        var resolved = PathConfinement.Resolve(root, "a/b/c.txt");

        resolved.Should().Contain("c.txt");
        resolved.Should().StartWith(Path.GetFullPath(root));
    }

    [Fact]
    public void TryResolve_ShouldReturnFalse_ForEscapingPath()
    {
        var ok = PathConfinement.TryResolve(root, "../outside", out var resolved);

        ok.Should().BeFalse();
        resolved.Should().BeEmpty();
    }

    [Fact]
    public void TryResolve_ShouldReturnTrueAndPath_ForValidInput()
    {
        var ok = PathConfinement.TryResolve(root, "nested/ok.txt", out var resolved);

        ok.Should().BeTrue();
        resolved.Should().EndWith("ok.txt");
    }
}
