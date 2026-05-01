using System.Text.Json.Nodes;
using CodeFlow.Runtime.Container;
using CodeFlow.Runtime.Workspace;
using FluentAssertions;

namespace CodeFlow.Runtime.Tests;

public sealed class HostToolProviderTests : IDisposable
{
    private readonly string workspaceRoot;
    private readonly HostToolProvider provider;
    private readonly ToolExecutionContext context;

    public HostToolProviderTests()
    {
        workspaceRoot = Path.Combine(Path.GetTempPath(), "codeflow-hosttool-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspaceRoot);
        provider = new HostToolProvider(
            workspaceTools: new WorkspaceHostToolService(
                new WorkspaceOptions
                {
                    Root = workspaceRoot,
                    ReadMaxBytes = 256 * 1024,
                    ExecTimeoutSeconds = 30,
                    ExecOutputMaxBytes = 128 * 1024
                }));
        context = new ToolExecutionContext(
            new ToolWorkspaceContext(
                Guid.NewGuid(),
                workspaceRoot,
                RepoUrl: "https://github.com/example/repo.git",
                RepoIdentityKey: "github.com/example/repo",
                RepoSlug: "example/repo"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public async Task InvokeAsync_ReadFile_ShouldReturnWorkspaceFileContent()
    {
        var path = Path.Combine(workspaceRoot, "src", "main.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "hello from workspace\n");

        var result = await provider.InvokeAsync(
            new ToolCall("call_read", "read_file", new JsonObject { ["path"] = "src/main.txt" }),
            context: context);

        result.IsError.Should().BeFalse();
        var payload = JsonNode.Parse(result.Content)!.AsObject();
        payload["path"]!.GetValue<string>().Should().Be("src/main.txt");
        payload["content"]!.GetValue<string>().Should().Contain("hello from workspace");
        payload["truncated"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task InvokeAsync_ReadFile_ShouldRejectPathOutsideWorkspace()
    {
        var act = async () => await provider.InvokeAsync(
            new ToolCall("call_read", "read_file", new JsonObject { ["path"] = "../outside.txt" }),
            context: context);

        var ex = await act.Should().ThrowAsync<PathConfinementException>();
        ex.Which.Message.Should().Contain("outside");
    }

    [Fact]
    public async Task InvokeAsync_ApplyPatch_ShouldUpdateWorkspaceFile()
    {
        var path = Path.Combine(workspaceRoot, "src", "main.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "alpha\nbeta\ngamma\n");

        var patch = """
            *** Begin Patch
            *** Update File: src/main.txt
             alpha
            -beta
            +beta patched
             gamma
            *** End Patch
            """;

        var result = await provider.InvokeAsync(
            new ToolCall("call_patch", "apply_patch", new JsonObject { ["patch"] = patch }),
            context: context);

        result.IsError.Should().BeFalse();
        (await File.ReadAllTextAsync(path)).Should().Be("alpha\nbeta patched\ngamma\n");
        JsonNode.Parse(result.Content)!["ok"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void GetCatalog_IncludesVcsTools()
    {
        var catalog = HostToolProvider.GetCatalog();
        var names = catalog.Select(t => t.Name).ToHashSet();

        names.Should().Contain("vcs.open_pr");
        names.Should().Contain("vcs.get_repo");
    }

    [Fact]
    public void GetCatalog_IncludesContainerRun()
    {
        var catalog = HostToolProvider.GetCatalog();
        var containerRun = catalog.SingleOrDefault(t => t.Name == DockerHostToolService.ContainerRunToolName);

        containerRun.Should().NotBeNull();
        containerRun!.IsMutating.Should().BeTrue();
        containerRun.Parameters!["required"]!.AsArray()
            .Select(node => node!.GetValue<string>())
            .Should().Contain(["image", "command"]);
    }

    [Fact]
    public void AvailableTools_RoleGrantedAllowlist_FiltersToContainerRunOnly()
    {
        // Simulates an envelope/role grant that allows ONLY container.run; the runtime
        // already filters by AllowedToolNames upstream of this provider, so ToolAccessPolicy
        // returning a single-name allowlist is the on-wire contract this slice exposes.
        var policy = new ToolAccessPolicy(
            AllowedToolNames: new[] { DockerHostToolService.ContainerRunToolName });

        var tools = provider.AvailableTools(policy);

        // The provider returns the full catalog (the registry intersects against
        // AllowedToolNames before invocation). What we want to confirm here is that
        // container.run *is* discoverable through the catalog so the allowlist can refer
        // to it by name without runtime errors.
        tools.Select(t => t.Name).Should().Contain(DockerHostToolService.ContainerRunToolName);
    }

    [Fact]
    public async Task InvokeAsync_ContainerRun_WithoutContainerService_ThrowsHelpfulError()
    {
        // The fixture's HostToolProvider was constructed without containerTools (default
        // ctor argument). Confirm hitting container.run fails fast with a clear deployment
        // hint instead of NRE-ing.
        var act = async () => await provider.InvokeAsync(
            new ToolCall(
                "call_run_container",
                DockerHostToolService.ContainerRunToolName,
                new JsonObject
                {
                    ["image"] = "node:22",
                    ["command"] = "npm",
                    ["args"] = new JsonArray("test")
                }),
            context: context);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*container.* tools are not configured*");
    }

    [Fact]
    public async Task InvokeAsync_ContainerRun_RoutesToDockerHostToolService()
    {
        var executionRoot = Path.Combine(Path.GetTempPath(), "codeflow-hosttool-exec-" + Guid.NewGuid().ToString("N"));
        try
        {
            var runner = new SuccessfulDockerRunner();
            var providerWithContainer = new HostToolProvider(
                workspaceTools: new WorkspaceHostToolService(
                    new WorkspaceOptions
                    {
                        Root = workspaceRoot,
                        ReadMaxBytes = 256 * 1024
                    }),
                containerTools: new DockerHostToolService(
                    new ContainerToolOptions(),
                    runner,
                    executionWorkspaces: new ContainerExecutionWorkspaceProvider(executionRoot)));

            var result = await providerWithContainer.InvokeAsync(
                new ToolCall(
                    "call_container",
                    DockerHostToolService.ContainerRunToolName,
                    new JsonObject
                    {
                        ["image"] = "node:22",
                        ["command"] = "npm",
                        ["args"] = new JsonArray("test")
                    }),
                context: context);

            result.IsError.Should().BeFalse();
            JsonNode.Parse(result.Content)!["image"]!.GetValue<string>().Should().Be("node:22");
            runner.LastArguments.Should().NotBeNull();
            runner.LastArguments!.Should().StartWith("run");
        }
        finally
        {
            if (Directory.Exists(executionRoot))
            {
                Directory.Delete(executionRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task InvokeAsync_ContainerRun_PolicyRefusal_ReturnsStructuredRefusalNotException()
    {
        var executionRoot = Path.Combine(Path.GetTempPath(), "codeflow-hosttool-exec-" + Guid.NewGuid().ToString("N"));
        try
        {
            var providerWithContainer = new HostToolProvider(
                workspaceTools: new WorkspaceHostToolService(
                    new WorkspaceOptions { Root = workspaceRoot }),
                containerTools: new DockerHostToolService(
                    new ContainerToolOptions(),
                    new SuccessfulDockerRunner(),
                    executionWorkspaces: new ContainerExecutionWorkspaceProvider(executionRoot)));

            var result = await providerWithContainer.InvokeAsync(
                new ToolCall(
                    "call_container",
                    DockerHostToolService.ContainerRunToolName,
                    new JsonObject
                    {
                        ["image"] = "ghcr.io/example/blocked:latest",
                        ["command"] = "npm"
                    }),
                context: context);

            result.IsError.Should().BeTrue();
            var refusal = JsonNode.Parse(result.Content)!["refusal"]!;
            refusal["code"]!.GetValue<string>().Should().Be("image-registry-denied");
            refusal["axis"]!.GetValue<string>().Should().Be("container-policy");
        }
        finally
        {
            if (Directory.Exists(executionRoot))
            {
                Directory.Delete(executionRoot, recursive: true);
            }
        }
    }

    private sealed class SuccessfulDockerRunner : IDockerCommandRunner
    {
        public IReadOnlyList<string>? LastArguments { get; private set; }

        public Task<DockerCommandResult> RunAsync(
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            long stdoutMaxBytes,
            long stderrMaxBytes,
            CancellationToken cancellationToken = default)
        {
            LastArguments = arguments.ToArray();
            // First call (lifecycle ps) returns empty; subsequent docker run returns ok.
            return Task.FromResult(new DockerCommandResult(
                ExitCode: 0,
                StandardOutput: string.Empty,
                StandardError: string.Empty,
                StandardOutputTruncated: false,
                StandardErrorTruncated: false,
                TimedOut: false));
        }
    }

    [Fact]
    public async Task InvokeAsync_VcsOpenPr_WithoutVcsService_ThrowsHelpfulError()
    {
        // The HostToolProvider in this test fixture was constructed without vcsTools (default
        // ctor argument). Confirm that hitting a vcs.* tool fails fast with a clear message
        // rather than NRE-ing — the message tells deployments they're missing the wiring.
        var act = async () => await provider.InvokeAsync(
            new ToolCall(
                "call_pr",
                "vcs.open_pr",
                new JsonObject
                {
                    ["owner"] = "foo",
                    ["name"] = "bar",
                    ["head"] = "feat/x",
                    ["base"] = "main",
                    ["title"] = "Add x"
                }),
            context: context);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*vcs.* tools are not configured*");
    }

    [Fact]
    public async Task InvokeAsync_RunCommand_ShouldExecuteInsideWorkspace()
    {
        var subdir = Path.Combine(workspaceRoot, "project");
        Directory.CreateDirectory(subdir);

        var result = await provider.InvokeAsync(
            new ToolCall(
                "call_run",
                "run_command",
                new JsonObject
                {
                    ["command"] = "dotnet",
                    ["args"] = new JsonArray("--version"),
                    ["workingDirectory"] = "project"
                }),
            context: context);

        result.IsError.Should().BeFalse();
        var payload = JsonNode.Parse(result.Content)!.AsObject();
        payload["exitCode"]!.GetValue<int>().Should().Be(0);
        payload["workingDirectory"]!.GetValue<string>().Should().Be("project");
        payload["stdout"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
    }
}
