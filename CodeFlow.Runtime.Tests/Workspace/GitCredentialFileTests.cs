using CodeFlow.Runtime.Workspace;
using FluentAssertions;

namespace CodeFlow.Runtime.Tests.Workspace;

public sealed class GitCredentialFileTests : IDisposable
{
    private readonly string root;

    public GitCredentialFileTests()
    {
        root = Path.Combine(Path.GetTempPath(), $"codeflow-creds-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task WriteAsync_PersistsStoreFormatLines_OnePerCredential()
    {
        var traceId = Guid.NewGuid();
        var creds = new[]
        {
            new HostCredential("github.com", "x-access-token", "ghp_token123"),
            new HostCredential("gitlab.example.com", "oauth2", "glpat_token456"),
        };

        await GitCredentialFile.WriteAsync(root, traceId, creds);

        var path = GitCredentialFile.BuildPath(root, traceId);
        var lines = await File.ReadAllLinesAsync(path);
        lines.Should().Equal(
            "https://x-access-token:ghp_token123@github.com",
            "https://oauth2:glpat_token456@gitlab.example.com");
    }

    [Fact]
    public async Task WriteAsync_UrlEncodesUserinfoSegments_SoTokensWithReservedCharsRoundTrip()
    {
        var traceId = Guid.NewGuid();
        // Real GitHub PATs are alphanumeric, but the helper must defensively encode anything
        // git would otherwise reparse — `:`, `@`, `/`, `?`, `#`, `%`. If the helper ever emits
        // a literal `@` in the userinfo segment, git will treat the second `@` as the
        // host-separator and the credential lookup will fail in subtle ways.
        var creds = new[]
        {
            new HostCredential("git.internal", "user@org", "pa:ss/word?x=1"),
        };

        await GitCredentialFile.WriteAsync(root, traceId, creds);

        var contents = await File.ReadAllTextAsync(GitCredentialFile.BuildPath(root, traceId));
        contents.Should().Contain("user%40org");
        contents.Should().Contain("pa%3Ass%2Fword%3Fx%3D1");
        contents.Should().NotContain("user@org:");
    }

    [Fact]
    public async Task WriteAsync_EmptyCredentials_RemovesAnyExistingFile()
    {
        var traceId = Guid.NewGuid();
        await GitCredentialFile.WriteAsync(root, traceId,
            new[] { new HostCredential("github.com", "x-access-token", "tok") });
        File.Exists(GitCredentialFile.BuildPath(root, traceId)).Should().BeTrue();

        await GitCredentialFile.WriteAsync(root, traceId, Array.Empty<HostCredential>());

        File.Exists(GitCredentialFile.BuildPath(root, traceId)).Should().BeFalse();
    }

    [Fact]
    public async Task WriteAsync_OverwritesPriorContents_NotAppending()
    {
        var traceId = Guid.NewGuid();
        await GitCredentialFile.WriteAsync(root, traceId,
            new[] { new HostCredential("github.com", "x-access-token", "first") });
        await GitCredentialFile.WriteAsync(root, traceId,
            new[] { new HostCredential("github.com", "x-access-token", "second") });

        var contents = await File.ReadAllTextAsync(GitCredentialFile.BuildPath(root, traceId));
        contents.Should().Contain("second");
        contents.Should().NotContain("first");
    }

    [Fact]
    public async Task WriteAsync_SetsMode0600_OnUnix()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return; // Windows: file mode is a no-op; assertion would always pass trivially.
        }

        var traceId = Guid.NewGuid();
        await GitCredentialFile.WriteAsync(root, traceId,
            new[] { new HostCredential("github.com", "x-access-token", "tok") });

        var mode = File.GetUnixFileMode(GitCredentialFile.BuildPath(root, traceId));
        mode.Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite,
            "the credential file holds plain-text tokens; group/other reads must never be possible");
    }

    [Fact]
    public async Task TryRemove_ReturnsTrue_WhenFileExisted_AndDeletes()
    {
        var traceId = Guid.NewGuid();
        await GitCredentialFile.WriteAsync(root, traceId,
            new[] { new HostCredential("github.com", "x-access-token", "tok") });

        var removed = GitCredentialFile.TryRemove(root, traceId);

        removed.Should().BeTrue();
        File.Exists(GitCredentialFile.BuildPath(root, traceId)).Should().BeFalse();
    }

    [Fact]
    public void TryRemove_ReturnsFalse_WhenFileMissing()
    {
        GitCredentialFile.TryRemove(root, Guid.NewGuid()).Should().BeFalse();
    }

    [Fact]
    public void TryRemove_ReturnsFalse_WhenRootIsNullOrEmpty()
    {
        GitCredentialFile.TryRemove(null, Guid.NewGuid()).Should().BeFalse();
        GitCredentialFile.TryRemove("", Guid.NewGuid()).Should().BeFalse();
        GitCredentialFile.TryRemove("   ", Guid.NewGuid()).Should().BeFalse();
    }

    [Fact]
    public async Task Sweep_DeletesExpiredFiles_KeepsFreshOnes_IgnoresNonTraceNames()
    {
        var now = DateTimeOffset.UtcNow;
        var maxAge = TimeSpan.FromDays(14);

        var expiredTraceId = Guid.NewGuid();
        var freshTraceId = Guid.NewGuid();
        await GitCredentialFile.WriteAsync(root, expiredTraceId,
            new[] { new HostCredential("github.com", "x-access-token", "old") });
        await GitCredentialFile.WriteAsync(root, freshTraceId,
            new[] { new HostCredential("github.com", "x-access-token", "new") });
        // A non-trace-shaped file in the same root must be left alone — operators occasionally
        // drop notes here, and a noisy delete-or-warn-per-cycle is just bad neighbour behaviour.
        var README = Path.Combine(root, "README.md");
        await File.WriteAllTextAsync(README, "operator notes");
        File.SetLastWriteTimeUtc(README, DateTime.UtcNow.AddDays(-30));

        File.SetLastWriteTimeUtc(
            GitCredentialFile.BuildPath(root, expiredTraceId),
            now.UtcDateTime - maxAge - TimeSpan.FromHours(1));
        File.SetLastWriteTimeUtc(
            GitCredentialFile.BuildPath(root, freshTraceId),
            now.UtcDateTime - TimeSpan.FromHours(1));

        var deleted = GitCredentialFile.Sweep(root, maxAge, now);

        deleted.Should().Be(1);
        File.Exists(GitCredentialFile.BuildPath(root, expiredTraceId)).Should().BeFalse();
        File.Exists(GitCredentialFile.BuildPath(root, freshTraceId)).Should().BeTrue();
        File.Exists(README).Should().BeTrue();
    }

    [Fact]
    public void Sweep_ReturnsZero_WhenRootDoesNotExist()
    {
        var nonexistent = Path.Combine(Path.GetTempPath(), $"never-created-{Guid.NewGuid():N}");
        GitCredentialFile.Sweep(nonexistent, TimeSpan.FromDays(1), DateTimeOffset.UtcNow)
            .Should().Be(0);
    }

    [Fact]
    public void BuildPath_UsesTraceIdNFormat_LowercaseHex32()
    {
        var traceId = Guid.NewGuid();
        var path = GitCredentialFile.BuildPath(root, traceId);

        var name = Path.GetFileName(path);
        name.Should().HaveLength(32);
        name.Should().MatchRegex("^[0-9a-f]{32}$");
        name.Should().Be(traceId.ToString("N"));
    }
}
