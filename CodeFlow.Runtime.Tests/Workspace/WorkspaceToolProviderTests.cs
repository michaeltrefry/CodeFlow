using System.Text.Json.Nodes;
using CodeFlow.Runtime;
using CodeFlow.Runtime.Workspace;
using FluentAssertions;

namespace CodeFlow.Runtime.Tests.Workspace;

public sealed class WorkspaceToolProviderTests : IDisposable
{
    private readonly List<string> cleanupDirs = [];
    private readonly string root;
    private readonly WorkspaceOptions options;
    private readonly WorkspaceService workspaceService;
    private readonly WorkspaceToolProvider provider;

    public WorkspaceToolProviderTests()
    {
        root = NewTempDir("codeflow-wstool-root");
        Directory.CreateDirectory(Path.Combine(root, WorkspaceOptions.CacheDirectoryName));
        Directory.CreateDirectory(Path.Combine(root, WorkspaceOptions.WorkDirectoryName));

        options = new WorkspaceOptions
        {
            Root = root,
            ReadMaxBytes = 256,
            GitCommandTimeout = TimeSpan.FromMinutes(2),
        };
        var gitCli = new GitCli(options);
        workspaceService = new WorkspaceService(options, gitCli);
        provider = new WorkspaceToolProvider(workspaceService, options);
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

    private string MakeOrigin(string owner, string name, Action<string>? seed = null)
    {
        var repoDir = GitTestRepo.InitRepo("codeflow-wstorigin-" + owner + "-" + name);
        cleanupDirs.Add(repoDir);
        File.WriteAllText(Path.Combine(repoDir, "README.md"), "# test\n");
        GitTestRepo.RunGit(repoDir, "add", "README.md");
        GitTestRepo.RunGit(repoDir, "commit", "-m", "seed");
        seed?.Invoke(repoDir);

        var container = Path.Combine(NewTempDir("codeflow-wstexp"), owner);
        Directory.CreateDirectory(container);
        var cloneTarget = Path.Combine(container, name + ".git");
        GitTestRepo.RunGit(repoDir, "clone", "--bare", repoDir, cloneTarget);
        return new Uri(cloneTarget).AbsoluteUri;
    }

    private static ToolCall Call(string name, JsonObject args)
        => new($"call-{Guid.NewGuid():N}", name, args);

    [Fact]
    public async Task Open_returns_same_handle_for_repeat_calls_in_one_correlation()
    {
        var origin = MakeOrigin("acme", "widget");
        var ctx = new AgentInvocationContext(Guid.NewGuid());

        var first = await provider.InvokeAsync(
            Call(WorkspaceToolProvider.OpenToolName, new JsonObject { ["repoUrl"] = origin }),
            ctx);
        var firstPayload = JsonNode.Parse(first.Content)!.AsObject();

        var second = await provider.InvokeAsync(
            Call(WorkspaceToolProvider.OpenToolName, new JsonObject { ["repoUrl"] = origin }),
            ctx);
        var secondPayload = JsonNode.Parse(second.Content)!.AsObject();

        firstPayload["repoSlug"]!.GetValue<string>().Should().Be("acme-widget");
        secondPayload["currentBranch"]!.GetValue<string>()
            .Should().Be(firstPayload["currentBranch"]!.GetValue<string>());
    }

    [Fact]
    public async Task Open_is_isolated_across_correlations()
    {
        var origin = MakeOrigin("acme", "widget");
        var ctxA = new AgentInvocationContext(Guid.NewGuid());
        var ctxB = new AgentInvocationContext(Guid.NewGuid());

        var a = await provider.InvokeAsync(
            Call(WorkspaceToolProvider.OpenToolName, new JsonObject { ["repoUrl"] = origin }),
            ctxA);
        var b = await provider.InvokeAsync(
            Call(WorkspaceToolProvider.OpenToolName, new JsonObject { ["repoUrl"] = origin }),
            ctxB);

        JsonNode.Parse(a.Content)!["currentBranch"]!.GetValue<string>()
            .Should().NotBe(JsonNode.Parse(b.Content)!["currentBranch"]!.GetValue<string>());
    }

    [Fact]
    public async Task ListFiles_skips_git_directory_and_returns_sorted_paths()
    {
        var origin = MakeOrigin("acme", "alpha", seed: repo =>
        {
            Directory.CreateDirectory(Path.Combine(repo, "src"));
            File.WriteAllText(Path.Combine(repo, "src", "b.txt"), "B");
            File.WriteAllText(Path.Combine(repo, "src", "a.txt"), "A");
            GitTestRepo.RunGit(repo, "add", ".");
            GitTestRepo.RunGit(repo, "commit", "-m", "src files");
        });

        var ctx = new AgentInvocationContext(Guid.NewGuid());
        var open = await provider.InvokeAsync(
            Call(WorkspaceToolProvider.OpenToolName, new JsonObject { ["repoUrl"] = origin }),
            ctx);
        var repoSlug = JsonNode.Parse(open.Content)!["repoSlug"]!.GetValue<string>();

        var listResult = await provider.InvokeAsync(
            Call(WorkspaceToolProvider.ListFilesToolName, new JsonObject { ["repoSlug"] = repoSlug }),
            ctx);
        var files = JsonNode.Parse(listResult.Content)!.AsArray()
            .Select(n => n!.GetValue<string>())
            .ToArray();

        files.Should().Contain("README.md").And.Contain("src/a.txt").And.Contain("src/b.txt");
        files.Should().NotContain(p => p.StartsWith(".git/") || p == ".git");
        files.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task ReadFile_returns_file_content_under_cap()
    {
        var origin = MakeOrigin("acme", "widget", seed: repo =>
        {
            File.WriteAllText(Path.Combine(repo, "small.txt"), "hello world");
            GitTestRepo.RunGit(repo, "add", ".");
            GitTestRepo.RunGit(repo, "commit", "-m", "small file");
        });

        var ctx = new AgentInvocationContext(Guid.NewGuid());
        var open = await provider.InvokeAsync(
            Call(WorkspaceToolProvider.OpenToolName, new JsonObject { ["repoUrl"] = origin }),
            ctx);
        var repoSlug = JsonNode.Parse(open.Content)!["repoSlug"]!.GetValue<string>();

        var read = await provider.InvokeAsync(
            Call(WorkspaceToolProvider.ReadFileToolName, new JsonObject
            {
                ["repoSlug"] = repoSlug,
                ["path"] = "small.txt",
            }),
            ctx);

        read.IsError.Should().BeFalse();
        read.Content.Should().Be("hello world");
    }

    [Fact]
    public async Task ReadFile_returns_tool_error_when_file_exceeds_cap()
    {
        var origin = MakeOrigin("acme", "widget", seed: repo =>
        {
            File.WriteAllText(Path.Combine(repo, "big.txt"), new string('x', 1024));
            GitTestRepo.RunGit(repo, "add", ".");
            GitTestRepo.RunGit(repo, "commit", "-m", "big file");
        });

        var ctx = new AgentInvocationContext(Guid.NewGuid());
        var open = await provider.InvokeAsync(
            Call(WorkspaceToolProvider.OpenToolName, new JsonObject { ["repoUrl"] = origin }),
            ctx);
        var repoSlug = JsonNode.Parse(open.Content)!["repoSlug"]!.GetValue<string>();

        var read = await provider.InvokeAsync(
            Call(WorkspaceToolProvider.ReadFileToolName, new JsonObject
            {
                ["repoSlug"] = repoSlug,
                ["path"] = "big.txt",
            }),
            ctx);

        read.IsError.Should().BeTrue();
        read.Content.Should().Contain("read cap");
    }

    [Fact]
    public async Task PathConfinement_rejection_is_returned_as_tool_error_not_exception()
    {
        var origin = MakeOrigin("acme", "widget");
        var ctx = new AgentInvocationContext(Guid.NewGuid());
        var open = await provider.InvokeAsync(
            Call(WorkspaceToolProvider.OpenToolName, new JsonObject { ["repoUrl"] = origin }),
            ctx);
        var repoSlug = JsonNode.Parse(open.Content)!["repoSlug"]!.GetValue<string>();

        var read = await provider.InvokeAsync(
            Call(WorkspaceToolProvider.ReadFileToolName, new JsonObject
            {
                ["repoSlug"] = repoSlug,
                ["path"] = "../../../etc/passwd",
            }),
            ctx);

        read.IsError.Should().BeTrue();
        read.Content.Should().Contain("outside");
    }

    [Fact]
    public async Task ListFiles_returns_error_when_workspace_not_open_in_correlation()
    {
        var ctx = new AgentInvocationContext(Guid.NewGuid());
        var result = await provider.InvokeAsync(
            Call(WorkspaceToolProvider.ListFilesToolName, new JsonObject { ["repoSlug"] = "acme-widget" }),
            ctx);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("not open");
    }

    [Fact]
    public void AvailableTools_respects_host_category_limit()
    {
        var zero = provider.AvailableTools(new ToolAccessPolicy(
            CategoryToolLimits: new Dictionary<ToolCategory, int> { [ToolCategory.Host] = 0 }));
        zero.Should().BeEmpty();

        var two = provider.AvailableTools(new ToolAccessPolicy(
            CategoryToolLimits: new Dictionary<ToolCategory, int> { [ToolCategory.Host] = 2 }));
        two.Should().HaveCount(2);
    }
}
