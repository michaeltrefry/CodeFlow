using CodeFlow.Runtime.Workspace;
using FluentAssertions;

namespace CodeFlow.Runtime.Tests.Workspace;

public sealed class GitCliTests : IDisposable
{
    private readonly List<string> cleanupDirs = [];

    public void Dispose()
    {
        foreach (var dir in cleanupDirs)
        {
            GitTestRepo.SafeDelete(dir);
        }
    }

    private GitCli CreateCli(TimeSpan? timeout = null)
    {
        var root = NewTempDir("codeflow-gitcli-root");
        return new GitCli(new WorkspaceOptions
        {
            Root = root,
            GitCommandTimeout = timeout ?? TimeSpan.FromMinutes(1)
        });
    }

    private string NewTempDir(string prefix)
    {
        var dir = GitTestRepo.CreateTempDirectory(prefix);
        cleanupDirs.Add(dir);
        return dir;
    }

    private string InitRepo(string prefix = "codeflow-gitcli")
    {
        var dir = GitTestRepo.InitRepo(prefix);
        cleanupDirs.Add(dir);
        return dir;
    }

    [Fact]
    public async Task RevParseAsync_ShouldReturnHeadSha_ForInitialCommit()
    {
        var cli = CreateCli();
        var repo = InitRepo();

        var sha = await cli.RevParseAsync(repo, "HEAD");

        sha.Should().MatchRegex("^[0-9a-f]{40}$");
    }

    [Fact]
    public async Task RevParseAsync_ShouldThrowGitCommandException_OnUnknownRef()
    {
        var cli = CreateCli();
        var repo = InitRepo();

        var act = () => cli.RevParseAsync(repo, "does-not-exist");

        var thrown = await act.Should().ThrowAsync<GitCommandException>();
        thrown.Which.ExitCode.Should().NotBe(0);
    }

    [Fact]
    public async Task ArgumentList_ShouldNotInvokeShell_WhenFilenameContainsShellMetacharacters()
    {
        var cli = CreateCli();
        var repo = InitRepo();

        var canary = "pwned.txt";
        var canaryPath = Path.Combine(repo, canary);

        // If args went through a shell, this would execute `touch pwned.txt` in the repo cwd
        // and create the canary. With arg-array, git receives this as a literal filename that
        // does not exist, fails, and the canary stays absent.
        var hostile = $"; touch {canary};";

        var act = () => cli.AddAsync(repo, [hostile]);
        await act.Should().ThrowAsync<GitCommandException>();

        File.Exists(canaryPath).Should().BeFalse(
            "arg-array invocation must not permit shell interpretation of filenames");
    }

    [Fact]
    public async Task CommitAsync_ShouldReturnFalse_WhenNothingToCommit()
    {
        var cli = CreateCli();
        var repo = InitRepo();

        var result = await cli.CommitAsync(repo, "no-op");

        result.Should().BeFalse();
    }

    [Fact]
    public async Task CommitAsync_ShouldReturnTrueAndAdvanceHead_WhenStagedChangesExist()
    {
        var cli = CreateCli();
        var repo = InitRepo();

        var initialHead = await cli.RevParseAsync(repo, "HEAD");

        await File.WriteAllTextAsync(Path.Combine(repo, "new.txt"), "hello");
        await cli.AddAsync(repo, ["new.txt"]);

        var committed = await cli.CommitAsync(repo, "add file");

        committed.Should().BeTrue();
        (await cli.RevParseAsync(repo, "HEAD")).Should().NotBe(initialHead);
    }

    [Fact]
    public async Task CloneAsync_LeavesNoAuthInGitConfig_WhenCalledWithCleanUrl()
    {
        // sc-662 regression: vcs.clone now calls IGitCli.CloneAsync with the *clean* URL the
        // agent provided — no provider.BuildAuthenticatedCloneUrl, no userinfo segment, no
        // post-clone `git remote set-url` scrub. Verify here at the IGitCli boundary that the
        // resulting `.git/config` carries the URL exactly as supplied, with no auth wrapping
        // anything subsequent ops in the workspace tree could surface.
        var cli = CreateCli();
        var source = InitRepo();
        await File.WriteAllTextAsync(Path.Combine(source, "f.txt"), "x");
        await cli.AddAsync(source, ["f.txt"]);
        await cli.CommitAsync(source, "add f");

        var destination = Path.Combine(NewTempDir("codeflow-clone-dest"), "clone");
        var sourceUrl = new Uri(source).AbsoluteUri; // file:///... — same shape as a clean http(s) URL

        await cli.CloneAsync(sourceUrl, destination);

        var config = await File.ReadAllTextAsync(Path.Combine(destination, ".git", "config"));
        config.Should().Contain(sourceUrl,
            "the clean URL must land in .git/config so subsequent run_command git ops use the credential helper for auth");
        config.Should().NotContain("@",
            "no userinfo segment — the embed-then-scrub flow is gone (sc-662); auth flows through credential.helper at runtime");
    }

    [Fact]
    public async Task CloneMirrorAsync_ShouldProduceBareMirror()
    {
        var cli = CreateCli();
        var source = InitRepo();
        await File.WriteAllTextAsync(Path.Combine(source, "f.txt"), "x");
        await cli.AddAsync(source, ["f.txt"]);
        await cli.CommitAsync(source, "add f");

        var mirror = Path.Combine(NewTempDir("codeflow-mirror"), "mirror.git");

        await cli.CloneMirrorAsync(source, mirror);

        Directory.Exists(mirror).Should().BeTrue();
        File.Exists(Path.Combine(mirror, "HEAD")).Should().BeTrue();
        Directory.Exists(Path.Combine(mirror, "refs", "heads")).Should().BeTrue();
    }

    [Fact]
    public async Task WorktreeAddAsync_ShouldCreateWorktreeCheckedOutOnBranch()
    {
        var cli = CreateCli();
        var source = InitRepo();
        await File.WriteAllTextAsync(Path.Combine(source, "f.txt"), "x");
        await cli.AddAsync(source, ["f.txt"]);
        await cli.CommitAsync(source, "add f");

        var mirror = Path.Combine(NewTempDir("codeflow-mirror"), "mirror.git");
        await cli.CloneMirrorAsync(source, mirror);

        var worktree = Path.Combine(NewTempDir("codeflow-worktree"), "wt");

        await cli.WorktreeAddAsync(mirror, worktree, "codeflow/work", startPoint: "main");

        Directory.Exists(worktree).Should().BeTrue();
        File.Exists(Path.Combine(worktree, "f.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task LsFilesAsync_ShouldReturnTrackedFiles()
    {
        var cli = CreateCli();
        var repo = InitRepo();
        await File.WriteAllTextAsync(Path.Combine(repo, "a.txt"), "x");
        await File.WriteAllTextAsync(Path.Combine(repo, "b.txt"), "y");
        await cli.AddAsync(repo, ["a.txt", "b.txt"]);
        await cli.CommitAsync(repo, "two files");

        var files = await cli.LsFilesAsync(repo);

        files.Should().Contain("a.txt").And.Contain("b.txt");
    }

    [Fact]
    public async Task StatusAsync_ShouldReturnEntries_ForUntrackedAndModified()
    {
        var cli = CreateCli();
        var repo = InitRepo();
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "original");
        await cli.AddAsync(repo, ["tracked.txt"]);
        await cli.CommitAsync(repo, "initial tracked");

        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "changed");
        await File.WriteAllTextAsync(Path.Combine(repo, "untracked.txt"), "new");

        var entries = await cli.StatusAsync(repo);

        entries.Select(e => e.Path).Should().Contain("tracked.txt").And.Contain("untracked.txt");
    }
}
