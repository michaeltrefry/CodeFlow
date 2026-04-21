using CodeFlow.Runtime.Workspace;
using FluentAssertions;

namespace CodeFlow.Runtime.Tests.Workspace;

public sealed class WorkspaceServiceTests : IDisposable
{
    private readonly List<string> cleanupDirs = [];
    private readonly string root;
    private readonly WorkspaceOptions options;
    private readonly IGitCli git;

    public WorkspaceServiceTests()
    {
        root = NewTempDir("codeflow-ws-root");
        Directory.CreateDirectory(Path.Combine(root, WorkspaceOptions.CacheDirectoryName));
        Directory.CreateDirectory(Path.Combine(root, WorkspaceOptions.WorkDirectoryName));

        options = new WorkspaceOptions
        {
            Root = root,
            GitCommandTimeout = TimeSpan.FromMinutes(2),
        };
        git = new GitCli(options);
    }

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

    private string InitOriginRepo(string nameHint = "origin")
    {
        var repoDir = GitTestRepo.InitRepo("codeflow-wsorigin-" + nameHint);
        cleanupDirs.Add(repoDir);
        File.WriteAllText(Path.Combine(repoDir, "README.md"), "# test\n");
        GitTestRepo.RunGit(repoDir, "add", "README.md");
        GitTestRepo.RunGit(repoDir, "commit", "-m", "add readme");
        return repoDir;
    }

    private string ToFileUrl(string repoDir, string owner, string name)
    {
        var container = Path.Combine(NewTempDir("codeflow-wsexpose"), owner);
        Directory.CreateDirectory(container);
        var cloneTarget = Path.Combine(container, name + ".git");
        GitTestRepo.RunGit(repoDir, "clone", "--bare", repoDir, cloneTarget);
        return new Uri(cloneTarget).AbsoluteUri;
    }

    private WorkspaceService CreateService() => new(options, git);

    [Fact]
    public async Task OpenAsync_ShouldCreateIndependentWorktrees_ForDifferentCorrelations()
    {
        var origin = ToFileUrl(InitOriginRepo(), "acme", "widget");
        var service = CreateService();

        var correlationA = Guid.NewGuid();
        var correlationB = Guid.NewGuid();

        var a = await service.OpenAsync(correlationA, origin);
        var b = await service.OpenAsync(correlationB, origin);

        a.RootPath.Should().NotBe(b.RootPath);
        a.CurrentBranch.Should().NotBe(b.CurrentBranch);
        Directory.Exists(a.RootPath).Should().BeTrue();
        Directory.Exists(b.RootPath).Should().BeTrue();

        var mirrorPaths = Directory.GetDirectories(Path.Combine(root, WorkspaceOptions.CacheDirectoryName), "*.git", SearchOption.AllDirectories);
        mirrorPaths.Should().HaveCount(1);
    }

    [Fact]
    public async Task OpenAsync_ShouldCreateSiblingWorktrees_ForDifferentReposInSameCorrelation()
    {
        var originA = ToFileUrl(InitOriginRepo("a"), "acme", "alpha");
        var originB = ToFileUrl(InitOriginRepo("b"), "acme", "beta");
        var service = CreateService();
        var correlation = Guid.NewGuid();

        var a = await service.OpenAsync(correlation, originA);
        var b = await service.OpenAsync(correlation, originB);

        Path.GetDirectoryName(a.RootPath).Should().Be(Path.GetDirectoryName(b.RootPath));
        a.RepoSlug.Should().NotBe(b.RepoSlug);
        Directory.Exists(a.RootPath).Should().BeTrue();
        Directory.Exists(b.RootPath).Should().BeTrue();
    }

    [Fact]
    public async Task OpenAsync_ShouldBeIdempotent_ForSameCorrelationAndRepo()
    {
        var origin = ToFileUrl(InitOriginRepo(), "acme", "widget");
        var service = CreateService();
        var correlation = Guid.NewGuid();

        var first = await service.OpenAsync(correlation, origin);
        var second = await service.OpenAsync(correlation, origin);

        second.Should().BeSameAs(first);
    }

    [Fact]
    public async Task OpenAsync_ShouldCloneMirrorOnce_WhenCalledInParallelForSameRepo()
    {
        var origin = ToFileUrl(InitOriginRepo(), "acme", "widget");
        var service = CreateService();

        var correlations = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        var tasks = correlations.Select(c => service.OpenAsync(c, origin)).ToArray();

        var workspaces = await Task.WhenAll(tasks);

        workspaces.Select(w => w.RootPath).Distinct().Should().HaveCount(correlations.Length);

        var mirrorPaths = Directory.GetDirectories(
            Path.Combine(root, WorkspaceOptions.CacheDirectoryName),
            "*.git",
            SearchOption.AllDirectories);
        mirrorPaths.Should().HaveCount(1);
    }

    [Fact]
    public async Task ReleaseAsync_ShouldRemoveOnlyTargetCorrelationWorktree_AndPreserveCache()
    {
        var origin = ToFileUrl(InitOriginRepo(), "acme", "widget");
        var service = CreateService();
        var correlationA = Guid.NewGuid();
        var correlationB = Guid.NewGuid();

        var a = await service.OpenAsync(correlationA, origin);
        var b = await service.OpenAsync(correlationB, origin);

        await service.ReleaseAsync(correlationA);

        Directory.Exists(a.RootPath).Should().BeFalse();
        Directory.Exists(b.RootPath).Should().BeTrue();

        var mirrorPaths = Directory.GetDirectories(
            Path.Combine(root, WorkspaceOptions.CacheDirectoryName),
            "*.git",
            SearchOption.AllDirectories);
        mirrorPaths.Should().HaveCount(1);

        service.Get(correlationA, a.RepoSlug).Should().BeNull();
        service.Get(correlationB, b.RepoSlug).Should().NotBeNull();
    }

    [Fact]
    public async Task OpenAsync_ShouldResolveDefaultBranch_FromMirrorHead()
    {
        var origin = ToFileUrl(InitOriginRepo(), "acme", "widget");
        var service = CreateService();

        var workspace = await service.OpenAsync(Guid.NewGuid(), origin);

        workspace.DefaultBranch.Should().Be("main");
    }
}
