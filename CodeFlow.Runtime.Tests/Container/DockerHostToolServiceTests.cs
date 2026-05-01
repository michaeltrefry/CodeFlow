using System.Text.Json.Nodes;
using CodeFlow.Runtime.Container;
using FluentAssertions;

namespace CodeFlow.Runtime.Tests.Container;

public sealed class DockerHostToolServiceTests : IDisposable
{
    private readonly string workspaceRoot;
    private readonly string executionRoot;
    private readonly ContainerExecutionWorkspaceProvider executionWorkspaces;

    public DockerHostToolServiceTests()
    {
        workspaceRoot = Path.Combine(Path.GetTempPath(), "codeflow-docker-tool-" + Guid.NewGuid().ToString("N"));
        executionRoot = Path.Combine(Path.GetTempPath(), "codeflow-docker-exec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(workspaceRoot, "src"));
        File.WriteAllText(Path.Combine(workspaceRoot, "src", "index.ts"), "console.log('hello');\n");
        executionWorkspaces = new ContainerExecutionWorkspaceProvider(executionRoot);
    }

    public void Dispose()
    {
        TryRemove(workspaceRoot);
        TryRemove(executionRoot);
    }

    private static void TryRemove(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public async Task RunContainerAsync_builds_constrained_docker_run_arguments()
    {
        var correlationId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var runner = new CapturingDockerRunner();
        var service = NewService(runner);

        var result = await service.RunContainerAsync(
            NewCall(new JsonObject
            {
                ["image"] = "node:22-bookworm",
                ["command"] = "npm",
                ["args"] = new JsonArray("test", "--", "--runInBand"),
                ["workingDirectory"] = "src",
                ["timeoutSeconds"] = 30
            }),
            NewContext(correlationId));

        result.IsError.Should().BeFalse();
        runner.Arguments.Should().NotBeNull();
        runner.Arguments!.Should().StartWith("run", "--rm");
        runner.Arguments.Should().ContainInOrder("--name", "codeflow-11111111222233334444555555555555-call_1-aaaaaaaaaaaa");
        runner.Calls.Should().HaveCount(2);
        runner.Calls[0].Arguments.Should().ContainInOrder(
            "ps",
            "-aq",
            "--filter",
            "label=codeflow.managed=true",
            "--filter",
            "label=codeflow.workflow=11111111222233334444555555555555");
        runner.Arguments.Should().ContainInOrder("--label", "codeflow.managed=true");
        runner.Arguments.Should().ContainInOrder("--label", "codeflow.workflow=11111111222233334444555555555555");
        runner.Arguments.Should().ContainInOrder("--label", "codeflow.createdAt=2026-05-01T13:00:00.0000000+00:00");
        runner.Arguments.Should().ContainInOrder("--label", "codeflow.resource=container");
        runner.Arguments.Should().ContainInOrder("--workdir", "/workspace/src");
        runner.Arguments.Should().ContainInOrder("--cpus", "2");
        runner.Arguments.Should().ContainInOrder("--memory", "4294967296");
        runner.Arguments.Should().ContainInOrder("--pids-limit", "1024");
        runner.Arguments.Should().ContainInOrder("--network", "bridge");
        var expectedExecPath = Path.Combine(executionRoot, "11111111222233334444555555555555");
        runner.Arguments.Should().ContainInOrder(
            "--mount",
            $"type=bind,source={expectedExecPath},target=/workspace,readonly=false");
        runner.Arguments.Should().NotContain($"type=bind,source={workspaceRoot},target=/workspace,readonly=false",
            because: "the canonical workspace must never be the docker bind-mount source");
        Directory.Exists(expectedExecPath).Should().BeTrue();
        File.ReadAllText(Path.Combine(expectedExecPath, "src", "index.ts"))
            .Should().Be("console.log('hello');\n", because: "execution workspace mirrors canonical contents");
        runner.Arguments.Should().ContainInOrder("node:22-bookworm", "npm", "test", "--", "--runInBand");
        runner.Timeout.Should().Be(TimeSpan.FromSeconds(30));

        var payload = JsonNode.Parse(result.Content)!;
        payload["ok"]!.GetValue<bool>().Should().BeTrue();
        payload["workingDirectory"]!.GetValue<string>().Should().Be("src");
        payload["containerName"]!.GetValue<string>().Should().Be("codeflow-11111111222233334444555555555555-call_1-aaaaaaaaaaaa");
    }

    [Theory]
    [InlineData("node:22")]
    [InlineData("library/node:22")]
    [InlineData("docker.io/library/node:22")]
    public async Task RunContainerAsync_allows_docker_hub_image_references(string image)
    {
        var runner = new CapturingDockerRunner();
        var service = NewService(runner);

        var result = await service.RunContainerAsync(
            NewCall(new JsonObject
            {
                ["image"] = image,
                ["command"] = "true"
            }),
            NewContext());

        result.IsError.Should().BeFalse();
        runner.Arguments.Should().Contain(image);
    }

    [Fact]
    public async Task RunContainerAsync_rejects_non_docker_hub_registries()
    {
        var service = NewService(new CapturingDockerRunner());

        var result = await service.RunContainerAsync(
            NewCall(new JsonObject
            {
                ["image"] = "ghcr.io/example/project:latest",
                ["command"] = "npm"
            }),
            NewContext());

        result.IsError.Should().BeTrue();
        JsonNode.Parse(result.Content)!["refusal"]!["code"]!.GetValue<string>()
            .Should().Be("image-registry-denied");
    }

    [Theory]
    [InlineData("docker", "build")]
    [InlineData("docker-compose", "up")]
    [InlineData("sh", "-c docker build .")]
    [InlineData("bash", "-lc docker compose up")]
    [InlineData("npm", "run build-Dockerfile")]
    public async Task RunContainerAsync_rejects_explicit_docker_build_or_compose_attempts(
        string command,
        string arg)
    {
        var runner = new CapturingDockerRunner();
        var service = NewService(runner);

        var result = await service.RunContainerAsync(
            NewCall(new JsonObject
            {
                ["image"] = "node:22",
                ["command"] = command,
                ["args"] = new JsonArray(arg)
            }),
            NewContext());

        result.IsError.Should().BeTrue();
        JsonNode.Parse(result.Content)!["refusal"]!["axis"]!.GetValue<string>()
            .Should().Be("container-policy");
        runner.Arguments.Should().BeNull();
    }

    [Fact]
    public async Task RunContainerAsync_rejects_working_directory_escape()
    {
        var runner = new CapturingDockerRunner();
        var service = NewService(runner);

        var result = await service.RunContainerAsync(
            NewCall(new JsonObject
            {
                ["image"] = "node:22",
                ["command"] = "npm",
                ["args"] = new JsonArray("test"),
                ["workingDirectory"] = "../outside"
            }),
            NewContext());

        result.IsError.Should().BeTrue();
        JsonNode.Parse(result.Content)!["refusal"]!["code"]!.GetValue<string>()
            .Should().Be("working-directory-confined");
        runner.Arguments.Should().BeNull();
    }

    [Fact]
    public async Task RunContainerAsync_caps_timeout_to_configured_max()
    {
        var runner = new CapturingDockerRunner();
        var service = NewService(runner);

        await service.RunContainerAsync(
            NewCall(new JsonObject
            {
                ["image"] = "node:22",
                ["command"] = "npm",
                ["args"] = new JsonArray("test"),
                ["timeoutSeconds"] = 9999
            }),
            NewContext());

        runner.Timeout.Should().Be(TimeSpan.FromSeconds(20 * 60));
    }

    [Fact]
    public async Task RunContainerAsync_refuses_when_workflow_container_limit_is_reached()
    {
        var runner = new CapturingDockerRunner
        {
            Results = new Queue<DockerCommandResult>([
                new DockerCommandResult(0, "c1\nc2\nc3\n", string.Empty, false, false, TimedOut: false)
            ])
        };
        var service = NewService(runner);

        var result = await service.RunContainerAsync(
            NewCall(new JsonObject
            {
                ["image"] = "node:22",
                ["command"] = "npm",
                ["args"] = new JsonArray("test")
            }),
            NewContext());

        result.IsError.Should().BeTrue();
        JsonNode.Parse(result.Content)!["refusal"]!["code"]!.GetValue<string>()
            .Should().Be("container-limit-exceeded");
        runner.Calls.Should().HaveCount(1);
    }

    [Fact]
    public async Task RunContainerAsync_mounts_execution_copy_so_simulated_writes_do_not_pollute_canonical()
    {
        var correlationId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var runner = new CapturingDockerRunner();
        var service = NewService(runner);

        await service.RunContainerAsync(
            NewCall(new JsonObject
            {
                ["image"] = "node:22",
                ["command"] = "npm",
                ["args"] = new JsonArray("test")
            }),
            NewContext(correlationId));

        var execPath = Path.Combine(executionRoot, correlationId.ToString("N"));
        Directory.Exists(execPath).Should().BeTrue();

        // Simulate the build container writing artifacts and overwriting a source file in /workspace.
        Directory.CreateDirectory(Path.Combine(execPath, "dist"));
        File.WriteAllText(Path.Combine(execPath, "dist", "bundle.js"), "/*compiled*/");
        File.WriteAllText(Path.Combine(execPath, "src", "index.ts"), "// container-edited\n");

        File.ReadAllText(Path.Combine(workspaceRoot, "src", "index.ts"))
            .Should().Be("console.log('hello');\n", because: "container writes must not pollute the canonical workspace");
        Directory.Exists(Path.Combine(workspaceRoot, "dist")).Should().BeFalse();
    }

    [Fact]
    public async Task RunContainerAsync_refuses_when_canonical_workspace_does_not_exist()
    {
        var runner = new CapturingDockerRunner();
        var service = NewService(runner);
        var missingPath = Path.Combine(Path.GetTempPath(), "codeflow-missing-" + Guid.NewGuid().ToString("N"));

        var result = await service.RunContainerAsync(
            NewCall(new JsonObject
            {
                ["image"] = "node:22",
                ["command"] = "npm"
            }),
            new ToolExecutionContext(Workspace: new ToolWorkspaceContext(Guid.NewGuid(), missingPath)));

        result.IsError.Should().BeTrue();
        JsonNode.Parse(result.Content)!["refusal"]!["code"]!.GetValue<string>()
            .Should().Be("workspace-not-ready");
    }

    [Fact]
    public async Task RunContainerAsync_marks_nonzero_exit_as_error()
    {
        var runner = new CapturingDockerRunner
        {
            Results = new Queue<DockerCommandResult>([
                new DockerCommandResult(0, string.Empty, string.Empty, false, false, TimedOut: false),
                new DockerCommandResult(ExitCode: 2, "out", "err", false, false, TimedOut: false)
            ])
        };
        var service = NewService(runner);

        var result = await service.RunContainerAsync(
            NewCall(new JsonObject
            {
                ["image"] = "node:22",
                ["command"] = "npm",
                ["args"] = new JsonArray("test")
            }),
            NewContext());

        result.IsError.Should().BeTrue();
        var payload = JsonNode.Parse(result.Content)!;
        payload["ok"]!.GetValue<bool>().Should().BeFalse();
        payload["exitCode"]!.GetValue<int>().Should().Be(2);
        payload["stderr"]!.GetValue<string>().Should().Be("err");
    }

    private DockerHostToolService NewService(CapturingDockerRunner runner) =>
        new(
            new ContainerToolOptions(),
            runner,
            executionWorkspaces: executionWorkspaces,
            idProvider: () => Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            nowProvider: () => DateTimeOffset.Parse("2026-05-01T13:00:00Z"));

    private ToolCall NewCall(JsonNode arguments) =>
        new("call_1", DockerHostToolService.ContainerRunToolName, arguments);

    private ToolExecutionContext NewContext(Guid? correlationId = null) =>
        new(Workspace: new ToolWorkspaceContext(correlationId ?? Guid.NewGuid(), workspaceRoot));

    private sealed class CapturingDockerRunner : IDockerCommandRunner
    {
        public IReadOnlyList<string>? Arguments { get; private set; }

        public TimeSpan Timeout { get; private set; }

        public List<CapturedDockerCall> Calls { get; } = [];

        public DockerCommandResult Result { get; set; } =
            new(ExitCode: 0, "ok", string.Empty, false, false, TimedOut: false);

        public Queue<DockerCommandResult>? Results { get; set; }

        public Task<DockerCommandResult> RunAsync(
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            long stdoutMaxBytes,
            long stderrMaxBytes,
            CancellationToken cancellationToken = default)
        {
            Arguments = arguments.ToArray();
            Timeout = timeout;
            Calls.Add(new CapturedDockerCall(arguments.ToArray(), timeout));
            if (Results is { Count: > 0 })
            {
                return Task.FromResult(Results.Dequeue());
            }

            return Task.FromResult(Result);
        }
    }

    private sealed record CapturedDockerCall(IReadOnlyList<string> Arguments, TimeSpan Timeout);
}
