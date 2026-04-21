using System.Text.Json.Nodes;
using CodeFlow.Runtime;
using CodeFlow.Runtime.Workspace;
using FluentAssertions;

namespace CodeFlow.Runtime.Tests.Workspace;

public sealed class VcsToolProviderTests : IDisposable
{
    private readonly List<string> cleanupDirs = [];
    private readonly string root;
    private readonly WorkspaceOptions options;
    private readonly GitCli gitCli;
    private readonly WorkspaceService workspaceService;

    public VcsToolProviderTests()
    {
        root = NewTempDir("codeflow-vcstool-root");
        Directory.CreateDirectory(Path.Combine(root, WorkspaceOptions.CacheDirectoryName));
        Directory.CreateDirectory(Path.Combine(root, WorkspaceOptions.WorkDirectoryName));

        options = new WorkspaceOptions
        {
            Root = root,
            GitCommandTimeout = TimeSpan.FromMinutes(2),
        };
        gitCli = new GitCli(options);
        workspaceService = new WorkspaceService(options, gitCli);
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

    private string MakeOrigin(string owner, string name)
    {
        var repoDir = GitTestRepo.InitRepo("codeflow-vcstorigin-" + owner + "-" + name);
        cleanupDirs.Add(repoDir);
        File.WriteAllText(Path.Combine(repoDir, "README.md"), "# test\n");
        GitTestRepo.RunGit(repoDir, "add", "README.md");
        GitTestRepo.RunGit(repoDir, "commit", "-m", "seed");

        var container = Path.Combine(NewTempDir("codeflow-vcstexp"), owner);
        Directory.CreateDirectory(container);
        var cloneTarget = Path.Combine(container, name + ".git");
        GitTestRepo.RunGit(repoDir, "clone", "--bare", repoDir, cloneTarget);
        return new Uri(cloneTarget).AbsoluteUri;
    }

    private VcsToolProvider CreateProvider(FakeVcsProvider? vcsProvider = null)
    {
        var provider = vcsProvider ?? new FakeVcsProvider();
        var factory = new FakeVcsProviderFactory(provider);
        var tokenProvider = new StaticTokenProvider("test-token");
        return new VcsToolProvider(workspaceService, gitCli, factory, tokenProvider);
    }

    private static ToolCall Call(string name, JsonObject args)
        => new($"call-{Guid.NewGuid():N}", name, args);

    private async Task<(AgentInvocationContext ctx, string repoSlug, CodeFlow.Runtime.Workspace.Workspace workspace)> OpenAsync(
        WorkspaceToolProvider wsProvider,
        string origin)
    {
        var ctx = new AgentInvocationContext(Guid.NewGuid());
        var open = await wsProvider.InvokeAsync(
            Call(WorkspaceToolProvider.OpenToolName, new JsonObject { ["repoUrl"] = origin }),
            ctx);
        var repoSlug = JsonNode.Parse(open.Content)!["repoSlug"]!.GetValue<string>();
        var workspace = workspaceService.Get(ctx.CorrelationId, repoSlug)!;
        return (ctx, repoSlug, workspace);
    }

    [Fact]
    public async Task CreateBranch_generates_default_name_when_not_supplied()
    {
        var origin = MakeOrigin("acme", "widget");
        var wsProvider = new WorkspaceToolProvider(workspaceService, options);
        var vcs = CreateProvider();

        var (ctx, repoSlug, _) = await OpenAsync(wsProvider, origin);
        var result = await vcs.InvokeAsync(
            Call(VcsToolProvider.CreateBranchToolName, new JsonObject { ["repoSlug"] = repoSlug }),
            ctx);

        result.IsError.Should().BeFalse();
        var branch = JsonNode.Parse(result.Content)!["branch"]!.GetValue<string>();
        branch.Should().StartWith("codeflow/");
    }

    [Fact]
    public async Task Commit_records_sha_when_staged_changes_exist()
    {
        var origin = MakeOrigin("acme", "widget");
        var wsProvider = new WorkspaceToolProvider(workspaceService, options);
        var vcs = CreateProvider();
        var (ctx, repoSlug, workspace) = await OpenAsync(wsProvider, origin);

        // Need a non-default branch because the default-branch push guard is separate,
        // but commit itself has no such restriction. Still, create a branch for realism.
        await vcs.InvokeAsync(
            Call(VcsToolProvider.CreateBranchToolName, new JsonObject
            {
                ["repoSlug"] = repoSlug,
                ["name"] = "feature/test",
            }),
            ctx);

        await wsProvider.InvokeAsync(
            Call(WorkspaceToolProvider.WriteFileToolName, new JsonObject
            {
                ["repoSlug"] = repoSlug,
                ["path"] = "NEW.md",
                ["content"] = "new content",
            }),
            ctx);

        var commit = await vcs.InvokeAsync(
            Call(VcsToolProvider.CommitToolName, new JsonObject
            {
                ["repoSlug"] = repoSlug,
                ["message"] = "add file",
            }),
            ctx);

        commit.IsError.Should().BeFalse();
        var payload = JsonNode.Parse(commit.Content)!.AsObject();
        payload["committed"]!.GetValue<bool>().Should().BeTrue();
        payload["sha"]!.GetValue<string>().Should().MatchRegex("^[0-9a-f]{40}$");
    }

    [Fact]
    public async Task Commit_returns_committed_false_when_no_changes()
    {
        var origin = MakeOrigin("acme", "widget");
        var wsProvider = new WorkspaceToolProvider(workspaceService, options);
        var vcs = CreateProvider();
        var (ctx, repoSlug, _) = await OpenAsync(wsProvider, origin);

        var commit = await vcs.InvokeAsync(
            Call(VcsToolProvider.CommitToolName, new JsonObject
            {
                ["repoSlug"] = repoSlug,
                ["message"] = "nothing",
            }),
            ctx);

        commit.IsError.Should().BeFalse();
        JsonNode.Parse(commit.Content)!["committed"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task Push_rejects_default_branch()
    {
        var origin = MakeOrigin("acme", "widget");
        var wsProvider = new WorkspaceToolProvider(workspaceService, options);
        var vcs = CreateProvider();
        var (ctx, repoSlug, workspace) = await OpenAsync(wsProvider, origin);

        // Checkout the default branch so CurrentBranch equals DefaultBranch.
        GitTestRepo.RunGit(workspace.RootPath, "checkout", workspace.DefaultBranch);

        var push = await vcs.InvokeAsync(
            Call(VcsToolProvider.PushToolName, new JsonObject { ["repoSlug"] = repoSlug }),
            ctx);

        push.IsError.Should().BeTrue();
        push.Content.Should().Contain("default branch");
    }

    [Fact]
    public async Task OpenPr_delegates_to_IVcsProvider_with_workspace_head_and_default_base()
    {
        var origin = MakeOrigin("acme", "widget");
        var wsProvider = new WorkspaceToolProvider(workspaceService, options);
        var fake = new FakeVcsProvider
        {
            OpenResult = new PullRequestInfo("https://example/prs/1", 1),
        };
        var vcs = CreateProvider(fake);
        var (ctx, repoSlug, workspace) = await OpenAsync(wsProvider, origin);

        await vcs.InvokeAsync(
            Call(VcsToolProvider.CreateBranchToolName, new JsonObject
            {
                ["repoSlug"] = repoSlug,
                ["name"] = "feature/x",
            }),
            ctx);

        var pr = await vcs.InvokeAsync(
            Call(VcsToolProvider.OpenPrToolName, new JsonObject
            {
                ["repoSlug"] = repoSlug,
                ["title"] = "My change",
                ["body"] = "details",
            }),
            ctx);

        pr.IsError.Should().BeFalse();
        JsonNode.Parse(pr.Content)!["url"]!.GetValue<string>().Should().Be("https://example/prs/1");

        fake.LastOpen!.Value.Owner.Should().Be("acme");
        fake.LastOpen.Value.Name.Should().Be("widget");
        fake.LastOpen.Value.Head.Should().Be("feature/x");
        fake.LastOpen.Value.Base.Should().Be(workspace.DefaultBranch);
    }

    [Fact]
    public async Task OpenPr_surfaces_VcsException_as_tool_error()
    {
        var origin = MakeOrigin("acme", "widget");
        var wsProvider = new WorkspaceToolProvider(workspaceService, options);
        var fake = new FakeVcsProvider
        {
            OpenException = new VcsConflictException("a pull request already exists"),
        };
        var vcs = CreateProvider(fake);
        var (ctx, repoSlug, _) = await OpenAsync(wsProvider, origin);

        await vcs.InvokeAsync(
            Call(VcsToolProvider.CreateBranchToolName, new JsonObject
            {
                ["repoSlug"] = repoSlug,
                ["name"] = "feature/x",
            }),
            ctx);

        var pr = await vcs.InvokeAsync(
            Call(VcsToolProvider.OpenPrToolName, new JsonObject
            {
                ["repoSlug"] = repoSlug,
                ["title"] = "t",
            }),
            ctx);

        pr.IsError.Should().BeTrue();
        pr.Content.Should().Contain("already exists");
    }

    [Fact]
    public void PushAsync_argument_list_does_not_accept_force_flag()
    {
        // Sanity: the interface does not expose a force parameter.
        var method = typeof(IGitCli).GetMethod(nameof(IGitCli.PushAsync));
        method!.GetParameters().Select(p => p.Name).Should().NotContain(p => p!.Contains("force", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class FakeVcsProvider : IVcsProvider
    {
        public GitHostMode Mode => GitHostMode.GitHub;

        public PullRequestInfo? OpenResult { get; set; }
        public Exception? OpenException { get; set; }
        public (string Owner, string Name, string Head, string Base)? LastOpen { get; private set; }

        public Task<VcsRepoMetadata> GetRepoMetadataAsync(string owner, string name, CancellationToken cancellationToken = default)
            => Task.FromResult(new VcsRepoMetadata("main", $"https://github.com/{owner}/{name}.git", VcsRepoVisibility.Private));

        public Task<PullRequestInfo> OpenPullRequestAsync(
            string owner, string name, string head, string baseRef, string title, string body,
            CancellationToken cancellationToken = default)
        {
            LastOpen = (owner, name, head, baseRef);
            if (OpenException is not null) throw OpenException;
            return Task.FromResult(OpenResult ?? new PullRequestInfo("https://example/prs/default", 1));
        }
    }

    private sealed class FakeVcsProviderFactory : IVcsProviderFactory
    {
        private readonly IVcsProvider provider;
        public FakeVcsProviderFactory(IVcsProvider provider) { this.provider = provider; }
        public Task<IVcsProvider> CreateAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(provider);
    }

    private sealed class StaticTokenProvider : IGitHostTokenProvider
    {
        private readonly string token;
        public StaticTokenProvider(string token) { this.token = token; }
        public Task<GitHostTokenLease> AcquireAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new GitHostTokenLease(token));
    }
}
