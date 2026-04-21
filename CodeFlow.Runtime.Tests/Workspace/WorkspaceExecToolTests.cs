using System.Text.Json.Nodes;
using CodeFlow.Runtime;
using CodeFlow.Runtime.Workspace;
using FluentAssertions;

namespace CodeFlow.Runtime.Tests.Workspace;

public sealed class WorkspaceExecToolTests : IDisposable
{
    private readonly List<string> cleanupDirs = [];
    private readonly string root;
    private readonly WorkspaceOptions options;
    private readonly WorkspaceService workspaceService;
    private readonly WorkspaceToolProvider provider;

    public WorkspaceExecToolTests()
    {
        root = NewTempDir("codeflow-wstexec-root");
        Directory.CreateDirectory(Path.Combine(root, WorkspaceOptions.CacheDirectoryName));
        Directory.CreateDirectory(Path.Combine(root, WorkspaceOptions.WorkDirectoryName));

        options = new WorkspaceOptions
        {
            Root = root,
            ExecTimeoutSeconds = 5,
            ExecOutputMaxBytes = 8192,
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

    private string MakeOrigin(string owner, string name)
    {
        var repoDir = GitTestRepo.InitRepo("codeflow-wstexecorigin-" + owner + "-" + name);
        cleanupDirs.Add(repoDir);
        File.WriteAllText(Path.Combine(repoDir, "README.md"), "# test\n");
        GitTestRepo.RunGit(repoDir, "add", "README.md");
        GitTestRepo.RunGit(repoDir, "commit", "-m", "seed");

        var container = Path.Combine(NewTempDir("codeflow-wstexecexp"), owner);
        Directory.CreateDirectory(container);
        var cloneTarget = Path.Combine(container, name + ".git");
        GitTestRepo.RunGit(repoDir, "clone", "--bare", repoDir, cloneTarget);
        return new Uri(cloneTarget).AbsoluteUri;
    }

    private static ToolCall Call(string name, JsonObject args)
        => new($"call-{Guid.NewGuid():N}", name, args);

    private async Task<(AgentInvocationContext ctx, string repoSlug, CodeFlow.Runtime.Workspace.Workspace workspace)> OpenAsync(string origin)
    {
        var ctx = new AgentInvocationContext(Guid.NewGuid());
        var open = await provider.InvokeAsync(
            Call(WorkspaceToolProvider.OpenToolName, new JsonObject { ["repoUrl"] = origin }),
            ctx);
        var repoSlug = JsonNode.Parse(open.Content)!["repoSlug"]!.GetValue<string>();
        var workspace = workspaceService.Get(ctx.CorrelationId, repoSlug)!;
        return (ctx, repoSlug, workspace);
    }

    [Fact]
    public async Task Exec_runs_simple_command_and_captures_stdout()
    {
        if (OperatingSystem.IsWindows()) return;

        var origin = MakeOrigin("acme", "exec");
        var (ctx, repoSlug, _) = await OpenAsync(origin);

        var result = await provider.InvokeAsync(
            Call(WorkspaceToolProvider.ExecToolName, new JsonObject
            {
                ["repoSlug"] = repoSlug,
                ["command"] = "/bin/echo",
                ["args"] = new JsonArray("hello", "world"),
            }),
            ctx);

        result.IsError.Should().BeFalse();
        var payload = JsonNode.Parse(result.Content)!.AsObject();
        payload["exitCode"]!.GetValue<int>().Should().Be(0);
        payload["stdout"]!.GetValue<string>().Should().Contain("hello world");
        payload["timedOut"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task Exec_timeout_kills_process_and_reports_timedOut()
    {
        if (OperatingSystem.IsWindows()) return;

        var origin = MakeOrigin("acme", "exec");
        var (ctx, repoSlug, _) = await OpenAsync(origin);

        var result = await provider.InvokeAsync(
            Call(WorkspaceToolProvider.ExecToolName, new JsonObject
            {
                ["repoSlug"] = repoSlug,
                ["command"] = "/bin/sleep",
                ["args"] = new JsonArray("10"),
                ["timeoutSeconds"] = 1,
            }),
            ctx);

        var payload = JsonNode.Parse(result.Content)!.AsObject();
        payload["timedOut"]!.GetValue<bool>().Should().BeTrue();
        payload["exitCode"]!.GetValue<int>().Should().Be(-1);
        payload["durationMs"]!.GetValue<int>().Should().BeLessThan(5000);
    }

    [Fact]
    public async Task Exec_env_allowlist_omits_vars_not_in_allowlist()
    {
        if (OperatingSystem.IsWindows()) return;

        Environment.SetEnvironmentVariable("CODEFLOW_TEST_LEAKY", "DO_NOT_LEAK");
        try
        {
            var origin = MakeOrigin("acme", "exec");
            var (ctx, repoSlug, _) = await OpenAsync(origin);

            var result = await provider.InvokeAsync(
                Call(WorkspaceToolProvider.ExecToolName, new JsonObject
                {
                    ["repoSlug"] = repoSlug,
                    ["command"] = "/usr/bin/env",
                }),
                ctx);

            var payload = JsonNode.Parse(result.Content)!.AsObject();
            var stdout = payload["stdout"]!.GetValue<string>();
            stdout.Should().NotContain("CODEFLOW_TEST_LEAKY");
            stdout.Should().NotContain("DO_NOT_LEAK");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEFLOW_TEST_LEAKY", null);
        }
    }

    [Fact]
    public async Task Exec_env_allowlist_includes_PATH_when_present()
    {
        if (OperatingSystem.IsWindows()) return;

        var origin = MakeOrigin("acme", "exec");
        var (ctx, repoSlug, _) = await OpenAsync(origin);

        var result = await provider.InvokeAsync(
            Call(WorkspaceToolProvider.ExecToolName, new JsonObject
            {
                ["repoSlug"] = repoSlug,
                ["command"] = "/usr/bin/env",
            }),
            ctx);

        var payload = JsonNode.Parse(result.Content)!.AsObject();
        payload["stdout"]!.GetValue<string>().Should().Contain("PATH=");
    }

    [Fact]
    public async Task Exec_arg_array_treats_shell_metacharacters_as_literal()
    {
        if (OperatingSystem.IsWindows()) return;

        var origin = MakeOrigin("acme", "exec");
        var (ctx, repoSlug, _) = await OpenAsync(origin);

        var result = await provider.InvokeAsync(
            Call(WorkspaceToolProvider.ExecToolName, new JsonObject
            {
                ["repoSlug"] = repoSlug,
                ["command"] = "/bin/echo",
                ["args"] = new JsonArray("; rm -rf /"),
            }),
            ctx);

        var payload = JsonNode.Parse(result.Content)!.AsObject();
        payload["exitCode"]!.GetValue<int>().Should().Be(0);
        payload["stdout"]!.GetValue<string>().Trim().Should().Be("; rm -rf /");
    }

    [Fact]
    public async Task Exec_runs_command_with_cwd_equal_to_workspace_root()
    {
        if (OperatingSystem.IsWindows()) return;

        var origin = MakeOrigin("acme", "exec");
        var (ctx, repoSlug, workspace) = await OpenAsync(origin);

        var result = await provider.InvokeAsync(
            Call(WorkspaceToolProvider.ExecToolName, new JsonObject
            {
                ["repoSlug"] = repoSlug,
                ["command"] = "/bin/pwd",
            }),
            ctx);

        var payload = JsonNode.Parse(result.Content)!.AsObject();
        var stdout = payload["stdout"]!.GetValue<string>().Trim();
        // macOS resolves /var -> /private/var; compare by repo-slug suffix which is unique per test.
        stdout.Should().EndWith(Path.Combine(workspace.CorrelationId.ToString("N"), workspace.RepoSlug));
    }

    [Fact]
    public async Task Exec_output_above_cap_is_truncated_with_marker()
    {
        if (OperatingSystem.IsWindows()) return;

        var tinyOptions = new WorkspaceOptions
        {
            Root = options.Root,
            ExecTimeoutSeconds = 2,
            ExecOutputMaxBytes = 256,
            GitCommandTimeout = options.GitCommandTimeout,
        };
        var tinyProvider = new WorkspaceToolProvider(workspaceService, tinyOptions);

        var origin = MakeOrigin("acme", "exec");
        var (ctx, repoSlug, _) = await OpenAsync(origin);

        var result = await tinyProvider.InvokeAsync(
            Call(WorkspaceToolProvider.ExecToolName, new JsonObject
            {
                ["repoSlug"] = repoSlug,
                ["command"] = "/usr/bin/yes",
                ["args"] = new JsonArray("hello"),
                ["timeoutSeconds"] = 1,
            }),
            ctx);

        var payload = JsonNode.Parse(result.Content)!.AsObject();
        payload["stdout"]!.GetValue<string>().Should().Contain("truncated");
    }

    [Fact]
    public async Task Exec_returns_tool_error_when_workspace_not_open()
    {
        var ctx = new AgentInvocationContext(Guid.NewGuid());
        var result = await provider.InvokeAsync(
            Call(WorkspaceToolProvider.ExecToolName, new JsonObject
            {
                ["repoSlug"] = "unknown",
                ["command"] = "/bin/echo",
            }),
            ctx);

        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task Exec_returns_tool_error_when_command_does_not_exist()
    {
        if (OperatingSystem.IsWindows()) return;

        var origin = MakeOrigin("acme", "exec");
        var (ctx, repoSlug, _) = await OpenAsync(origin);

        var result = await provider.InvokeAsync(
            Call(WorkspaceToolProvider.ExecToolName, new JsonObject
            {
                ["repoSlug"] = repoSlug,
                ["command"] = "/no/such/binary",
            }),
            ctx);

        result.IsError.Should().BeTrue();
    }
}
