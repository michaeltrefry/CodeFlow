using System.Text.Json.Nodes;
using CodeFlow.Runtime.Container;
using FluentAssertions;

namespace CodeFlow.Runtime.Tests.Container;

public sealed class DockerHostToolServiceTests : IDisposable
{
    private readonly string workspaceRoot;

    public DockerHostToolServiceTests()
    {
        workspaceRoot = Path.Combine(Path.GetTempPath(), "codeflow-docker-tool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(workspaceRoot, "src"));
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
        runner.Arguments.Should().ContainInOrder("--label", "codeflow.managed=true");
        runner.Arguments.Should().ContainInOrder("--label", "codeflow.workflow=11111111222233334444555555555555");
        runner.Arguments.Should().ContainInOrder("--workdir", "/workspace/src");
        runner.Arguments.Should().ContainInOrder("--cpus", "2");
        runner.Arguments.Should().ContainInOrder("--memory", "4294967296");
        runner.Arguments.Should().ContainInOrder("--pids-limit", "1024");
        runner.Arguments.Should().ContainInOrder("--network", "bridge");
        runner.Arguments.Should().ContainInOrder(
            "--mount",
            $"type=bind,source={workspaceRoot},target=/workspace,readonly=false");
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
    public async Task RunContainerAsync_marks_nonzero_exit_as_error()
    {
        var runner = new CapturingDockerRunner
        {
            Result = new DockerCommandResult(ExitCode: 2, "out", "err", false, false, TimedOut: false)
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
        new(new ContainerToolOptions(), runner, () => Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));

    private ToolCall NewCall(JsonNode arguments) =>
        new("call_1", DockerHostToolService.ContainerRunToolName, arguments);

    private ToolExecutionContext NewContext(Guid? correlationId = null) =>
        new(Workspace: new ToolWorkspaceContext(correlationId ?? Guid.NewGuid(), workspaceRoot));

    private sealed class CapturingDockerRunner : IDockerCommandRunner
    {
        public IReadOnlyList<string>? Arguments { get; private set; }

        public TimeSpan Timeout { get; private set; }

        public DockerCommandResult Result { get; set; } =
            new(ExitCode: 0, "ok", string.Empty, false, false, TimedOut: false);

        public Task<DockerCommandResult> RunAsync(
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            long stdoutMaxBytes,
            long stderrMaxBytes,
            CancellationToken cancellationToken = default)
        {
            Arguments = arguments.ToArray();
            Timeout = timeout;
            return Task.FromResult(Result);
        }
    }
}
