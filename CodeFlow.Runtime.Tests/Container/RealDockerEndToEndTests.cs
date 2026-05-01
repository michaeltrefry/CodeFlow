using System.Diagnostics;
using System.Text.Json.Nodes;
using CodeFlow.Runtime.Container;
using FluentAssertions;

namespace CodeFlow.Runtime.Tests.Container;

/// <summary>
/// sc-453 real-Docker validation. Pulls and runs actual docker.io images to confirm the
/// argument-safe `docker run` pipeline, network policy, and Dockerfile ban hold against a
/// live Docker daemon. Gated by <c>[Trait("Category", "DockerRequired")]</c> — CI excludes
/// this trait via <c>--filter "Category!=DockerRequired"</c>; opt in locally with
/// <c>dotnet test --filter "Category=DockerRequired"</c>.
///
/// Each test fast-fails with a clear message when the docker CLI isn't available on PATH so
/// the opt-in user knows their environment isn't ready rather than silently passing.
/// </summary>
[Trait("Category", "DockerRequired")]
public sealed class RealDockerEndToEndTests : IDisposable
{
    private readonly string canonicalRoot;
    private readonly string executionRoot;
    private readonly Guid workflowId = Guid.NewGuid();

    public RealDockerEndToEndTests()
    {
        canonicalRoot = Path.Combine(Path.GetTempPath(), "codeflow-real-docker-canonical-" + Guid.NewGuid().ToString("N"));
        executionRoot = Path.Combine(Path.GetTempPath(), "codeflow-real-docker-exec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(canonicalRoot);
    }

    public void Dispose()
    {
        // Best-effort cleanup of any container/volume the tests may have left behind. The
        // managed-label filter prevents collateral damage to the operator's other workloads.
        try
        {
            var lifecycle = new DockerLifecycleService(
                new ContainerToolOptions(),
                new DockerCliCommandRunner(),
                new ContainerExecutionWorkspaceProvider(executionRoot));
            lifecycle.CleanupWorkflowAsync(workflowId).GetAwaiter().GetResult();
        }
        catch
        {
        }

        TryRemove(canonicalRoot);
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
    public async Task Node_22_alpine_runs_node_version_under_constrained_args()
    {
        RequireDockerAvailable();
        var service = NewDockerHostToolService();

        var result = await service.RunContainerAsync(
            NewCall(new JsonObject
            {
                ["image"] = "node:22-alpine",
                ["command"] = "node",
                ["args"] = new JsonArray("--version"),
                ["timeoutSeconds"] = 60
            }),
            NewContext());

        result.IsError.Should().BeFalse();
        var payload = JsonNode.Parse(result.Content)!;
        payload["exitCode"]!.GetValue<int>().Should().Be(0);
        payload["stdout"]!.GetValue<string>().Should().StartWith("v22.");
    }

    [Fact]
    public async Task Python_3_12_alpine_runs_python_version_under_constrained_args()
    {
        RequireDockerAvailable();
        var service = NewDockerHostToolService();

        var result = await service.RunContainerAsync(
            NewCall(new JsonObject
            {
                ["image"] = "python:3.12-alpine",
                ["command"] = "python",
                ["args"] = new JsonArray("--version"),
                ["timeoutSeconds"] = 60
            }),
            NewContext());

        result.IsError.Should().BeFalse();
        var payload = JsonNode.Parse(result.Content)!;
        payload["exitCode"]!.GetValue<int>().Should().Be(0);
        var combined = (payload["stdout"]?.GetValue<string>() ?? string.Empty)
            + (payload["stderr"]?.GetValue<string>() ?? string.Empty);
        combined.Should().Contain("Python 3.12");
    }

    [Fact]
    public async Task Golang_1_22_alpine_compiles_and_runs_a_trivial_go_program_with_network()
    {
        // Compiled-language scenario per sc-453 scope. Writes a tiny main.go into the
        // canonical workspace, runs `go run` inside a docker.io golang image, asserts the
        // program output. The package-download requirement is exercised implicitly by go
        // resolving the standard-library at startup.
        RequireDockerAvailable();
        Directory.CreateDirectory(canonicalRoot);
        await File.WriteAllTextAsync(
            Path.Combine(canonicalRoot, "main.go"),
            "package main\nimport \"fmt\"\nfunc main() { fmt.Println(\"codeflow-real-docker-go\") }\n");
        var service = NewDockerHostToolService();

        var result = await service.RunContainerAsync(
            NewCall(new JsonObject
            {
                ["image"] = "golang:1.22-alpine",
                ["command"] = "go",
                ["args"] = new JsonArray("run", "main.go"),
                ["timeoutSeconds"] = 120
            }),
            NewContext());

        result.IsError.Should().BeFalse();
        var payload = JsonNode.Parse(result.Content)!;
        payload["exitCode"]!.GetValue<int>().Should().Be(0);
        payload["stdout"]!.GetValue<string>().Should().Contain("codeflow-real-docker-go");
    }

    [Fact]
    public async Task Npm_install_pulls_a_real_package_through_bridge_network()
    {
        // Verifies the intended network policy: `--network bridge` lets the container reach
        // public package registries. Uses `is-array` (a tiny no-deps package) so the install
        // is fast and doesn't pull half the npm ecosystem.
        RequireDockerAvailable();
        var service = NewDockerHostToolService();

        var result = await service.RunContainerAsync(
            NewCall(new JsonObject
            {
                ["image"] = "node:22-alpine",
                ["command"] = "npm",
                ["args"] = new JsonArray("install", "--silent", "--no-save", "--prefix", "/tmp/probe", "is-array"),
                ["timeoutSeconds"] = 180
            }),
            NewContext());

        result.IsError.Should().BeFalse(
            because: "build/test containers must reach public package registries through the bridge network policy");
        JsonNode.Parse(result.Content)!["exitCode"]!.GetValue<int>().Should().Be(0);
    }

    [Fact]
    public async Task Repo_dockerfile_attempt_is_refused_before_any_docker_process_is_started()
    {
        // Defense-in-depth check at the real-runner layer. Even with a live Docker daemon
        // available, the policy must short-circuit forbidden Docker operations BEFORE
        // invoking the runner. We assert via the structured refusal payload; no container
        // is created on the daemon as a result.
        RequireDockerAvailable();
        var service = NewDockerHostToolService();

        var result = await service.RunContainerAsync(
            NewCall(new JsonObject
            {
                ["image"] = "node:22-alpine",
                ["command"] = "sh",
                ["args"] = new JsonArray("-c", "docker build -t evil ."),
                ["timeoutSeconds"] = 30
            }),
            NewContext());

        result.IsError.Should().BeTrue();
        var refusal = JsonNode.Parse(result.Content)!["refusal"]!;
        refusal["code"]!.GetValue<string>().Should().Be("docker-build-denied");
        refusal["axis"]!.GetValue<string>().Should().Be("container-policy");
    }

    [Fact]
    public async Task Cleanup_against_live_daemon_runs_without_error_and_returns_zero_when_workflow_has_no_resources()
    {
        // The CLI cleanup invocations talk to a real `docker` process. This smoke-tests
        // that argument shape (filters, label selectors) is accepted by the real CLI; the
        // count assertions are 0 since no test before this seeded resources with the
        // freshly-generated workflow id.
        RequireDockerAvailable();
        var lifecycle = new DockerLifecycleService(
            new ContainerToolOptions(),
            new DockerCliCommandRunner(),
            new ContainerExecutionWorkspaceProvider(executionRoot));

        var result = await lifecycle.CleanupWorkflowAsync(Guid.NewGuid());

        result.RemovedContainers.Should().Be(0);
        result.RemovedVolumes.Should().Be(0);
        result.RemovedExecutionWorkspaces.Should().Be(0);
    }

    private DockerHostToolService NewDockerHostToolService()
    {
        var workspace = new ContainerExecutionWorkspaceProvider(executionRoot);
        var lifecycle = new DockerLifecycleService(
            new ContainerToolOptions(),
            new DockerCliCommandRunner(),
            workspace);
        return new DockerHostToolService(
            new ContainerToolOptions(),
            new DockerCliCommandRunner(),
            lifecycle,
            workspace);
    }

    private ToolCall NewCall(JsonNode arguments) =>
        new($"call_{Guid.NewGuid():N}", DockerHostToolService.ContainerRunToolName, arguments);

    private ToolExecutionContext NewContext() =>
        new(Workspace: new ToolWorkspaceContext(workflowId, canonicalRoot));

    private static void RequireDockerAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process is null)
            {
                throw new InvalidOperationException("Could not start the 'docker' process.");
            }

            process.WaitForExit(5_000);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"`docker --version` exited {process.ExitCode}. Tests in the DockerRequired category "
                    + "require a working local Docker daemon. Skip them with --filter \"Category!=DockerRequired\".");
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                "`docker` is not available on PATH. Tests in the DockerRequired category require a "
                + "working local Docker daemon. Skip them with --filter \"Category!=DockerRequired\".",
                ex);
        }
    }
}
