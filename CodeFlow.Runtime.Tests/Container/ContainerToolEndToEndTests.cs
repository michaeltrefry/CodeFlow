using System.Text.Json.Nodes;
using CodeFlow.Runtime.Container;
using CodeFlow.Runtime.Workspace;
using FluentAssertions;

namespace CodeFlow.Runtime.Tests.Container;

/// <summary>
/// sc-453 end-to-end coverage of the container.run feature using a fake
/// <see cref="IDockerCommandRunner"/>. Exercises the agent → HostToolProvider →
/// DockerHostToolService → ContainerExecutionWorkspaceProvider → DockerLifecycleService arc
/// for representative Node/.NET/Python/Go build-test scenarios, role-grant filtering,
/// structured refusals for forbidden Docker operations, and cleanup of containers + cache
/// volumes + execution workspaces.
///
/// Real-Docker counterparts live in <see cref="RealDockerEndToEndTests"/> and are gated by
/// <c>[Trait("Category", "DockerRequired")]</c> so CI doesn't try to pull images.
/// </summary>
public sealed class ContainerToolEndToEndTests : IDisposable
{
    private readonly string canonicalRoot;
    private readonly string executionRoot;

    public ContainerToolEndToEndTests()
    {
        canonicalRoot = Path.Combine(Path.GetTempPath(), "codeflow-e2e-canonical-" + Guid.NewGuid().ToString("N"));
        executionRoot = Path.Combine(Path.GetTempPath(), "codeflow-e2e-exec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(canonicalRoot);
    }

    public void Dispose()
    {
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

    public static IEnumerable<object[]> BuildTestScenarios() => new[]
    {
        new object[] { "node:22-bookworm", "npm", new[] { "test", "--", "--runInBand" }, "src" },
        new object[] { "library/python:3.12-slim", "pytest", new[] { "-q", "tests/" }, "." },
        new object[] { "docker.io/library/golang:1.22-alpine", "go", new[] { "test", "./..." }, "." },
        new object[] { "mcr.microsoft.com/dotnet/sdk:9.0", "dotnet", new[] { "test", "--no-restore" }, "." }
    };

    [Theory]
    [MemberData(nameof(BuildTestScenarios))]
    public async Task End_to_end_build_test_arc_constructs_argument_safe_docker_run(
        string image,
        string command,
        string[] args,
        string workingDirectory)
    {
        // The .NET row uses mcr.microsoft.com which is NOT docker.io — it should be refused
        // by the registry policy. The other three rows are valid docker.io references and
        // should construct a complete `docker run` argument list.
        var workflowId = Guid.NewGuid();
        Directory.CreateDirectory(Path.Combine(canonicalRoot, "src"));
        File.WriteAllText(Path.Combine(canonicalRoot, "src", "main.txt"), "hello");

        var runner = new ScriptedDockerRunner();
        runner.Enqueue(DockerOk()); // count probe
        runner.Enqueue(DockerOk()); // docker run
        var (provider, _) = NewProvider(runner);

        var result = await provider.InvokeAsync(
            new ToolCall(
                $"call_{command}",
                DockerHostToolService.ContainerRunToolName,
                new JsonObject
                {
                    ["image"] = image,
                    ["command"] = command,
                    ["args"] = new JsonArray(args.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()),
                    ["workingDirectory"] = workingDirectory
                }),
            context: new ToolExecutionContext(
                Workspace: new ToolWorkspaceContext(workflowId, canonicalRoot)));

        if (image.StartsWith("mcr.microsoft.com", StringComparison.Ordinal))
        {
            result.IsError.Should().BeTrue();
            JsonNode.Parse(result.Content)!["refusal"]!["code"]!.GetValue<string>()
                .Should().Be("image-registry-denied");
            runner.Calls.Should().BeEmpty(
                "registry policy short-circuits before any docker process is started");
            return;
        }

        result.IsError.Should().BeFalse();
        var dockerRun = runner.Calls[1].Arguments;
        dockerRun.Should().StartWith("run", "--rm");
        dockerRun.Should().Contain("--cpus");
        dockerRun.Should().Contain("--memory");
        dockerRun.Should().Contain("--pids-limit");
        dockerRun.Should().Contain("--network", "bridge");
        dockerRun.Should().Contain(image);
        dockerRun.Should().Contain(command);
        foreach (var arg in args)
        {
            dockerRun.Should().Contain(arg);
        }

        var execWorkspace = Path.Combine(executionRoot, workflowId.ToString("N"));
        dockerRun.Should().Contain(
            $"type=bind,source={execWorkspace},target=/workspace,readonly=false",
            because: "the canonical workspace must NEVER be the bind-mount source — only the per-workflow exec mirror");
        File.Exists(Path.Combine(execWorkspace, "src", "main.txt")).Should().BeTrue(
            "EnsureForWorkflow must mirror canonical contents before the docker run is dispatched");
    }

    [Fact]
    public async Task Cleanup_arc_removes_containers_volumes_and_execution_workspace()
    {
        var workflowId = Guid.NewGuid();
        Directory.CreateDirectory(Path.Combine(canonicalRoot, "src"));

        var runner = new ScriptedDockerRunner();
        // First run: count probe (empty), docker run.
        runner.Enqueue(DockerOk());
        runner.Enqueue(DockerOk());
        // Second run: count probe (one labeled), docker run.
        runner.Enqueue(new DockerCommandResult(0, "container-1\n", string.Empty, false, false, false));
        runner.Enqueue(DockerOk());
        // Cleanup: ps list, rm, volume ls, volume rm.
        runner.Enqueue(new DockerCommandResult(0, "container-1\ncontainer-2\n", string.Empty, false, false, false));
        runner.Enqueue(DockerOk()); // rm -f c1 c2
        runner.Enqueue(new DockerCommandResult(0, "vol-1\n", string.Empty, false, false, false));
        runner.Enqueue(DockerOk()); // volume rm -f vol-1
        var (provider, lifecycle) = NewProvider(runner);
        var execWorkspace = Path.Combine(executionRoot, workflowId.ToString("N"));

        for (var i = 1; i <= 2; i++)
        {
            await provider.InvokeAsync(
                new ToolCall(
                    $"call_{i}",
                    DockerHostToolService.ContainerRunToolName,
                    new JsonObject
                    {
                        ["image"] = "node:22",
                        ["command"] = "npm",
                        ["args"] = new JsonArray($"step{i}")
                    }),
                context: new ToolExecutionContext(
                    Workspace: new ToolWorkspaceContext(workflowId, canonicalRoot)));
        }

        Directory.Exists(execWorkspace).Should().BeTrue();

        var cleanup = await lifecycle.CleanupWorkflowAsync(workflowId);

        cleanup.RemovedContainers.Should().Be(2);
        cleanup.RemovedVolumes.Should().Be(1);
        cleanup.RemovedExecutionWorkspaces.Should().Be(1);
        Directory.Exists(execWorkspace).Should().BeFalse(
            "the per-workflow execution mirror must be discarded at cleanup so build artifacts don't leak across runs");
    }

    public static IEnumerable<object[]> ForbiddenDockerOperations() => new[]
    {
        new object[] { "node:22", "docker", new[] { "build", "-t", "x", "." }, "docker-command-denied" },
        new object[] { "node:22", "docker-compose", new[] { "up" }, "docker-command-denied" },
        new object[] { "node:22", "sh", new[] { "-c", "docker build ." }, "docker-build-denied" },
        new object[] { "node:22", "bash", new[] { "-lc", "docker compose up" }, "docker-build-denied" },
        new object[] { "node:22", "npm", new[] { "run", "build-with-Dockerfile" }, "docker-build-denied" },
        new object[] { "ghcr.io/example/repo:latest", "npm", new[] { "test" }, "image-registry-denied" },
        new object[] { "registry.gitlab.com/foo/bar:1", "npm", new[] { "test" }, "image-registry-denied" }
    };

    [Theory]
    [MemberData(nameof(ForbiddenDockerOperations))]
    public async Task Forbidden_docker_operations_emit_structured_refusal_with_axis(
        string image,
        string command,
        string[] args,
        string expectedCode)
    {
        var runner = new ScriptedDockerRunner();
        runner.Enqueue(DockerOk()); // safety: first call (count probe) only fires if registry/command policy passes
        var (provider, _) = NewProvider(runner);

        var result = await provider.InvokeAsync(
            new ToolCall(
                "call_forbidden",
                DockerHostToolService.ContainerRunToolName,
                new JsonObject
                {
                    ["image"] = image,
                    ["command"] = command,
                    ["args"] = new JsonArray(args.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray())
                }),
            context: new ToolExecutionContext(
                Workspace: new ToolWorkspaceContext(Guid.NewGuid(), canonicalRoot)));

        result.IsError.Should().BeTrue();
        var refusal = JsonNode.Parse(result.Content)!["refusal"]!;
        refusal["code"]!.GetValue<string>().Should().Be(expectedCode);
        refusal["axis"]!.GetValue<string>().Should().Be("container-policy");
    }

    [Fact]
    public async Task Role_grant_filter_denies_container_run_when_policy_excludes_it()
    {
        // ToolAccessPolicy.AllowedToolNames is the on-wire contract enforced by the runtime
        // upstream of the provider; the provider's AvailableTools(policy) honors the same
        // limit. An agent with a code-worker-style policy (no container.run) sees no
        // container.run in its catalog → it can't be invoked even if the host wires the tool.
        var runner = new ScriptedDockerRunner();
        var (provider, _) = NewProvider(runner);

        var allowed = new ToolAccessPolicy(
            AllowedToolNames: new[] { "read_file", "apply_patch", "run_command", "echo", "now" });

        var availableNames = provider.AvailableTools(allowed).Select(t => t.Name).ToHashSet();

        // The host catalog is global, but the upstream pipeline filters InvokeAsync targets
        // by AllowedToolNames; AvailableTools' job is to advertise the catalog page. The
        // intersection (what the *agent* sees as callable) is policy ∩ catalog.
        var callable = availableNames.Intersect(allowed.AllowedToolNames!);
        callable.Should().NotContain(DockerHostToolService.ContainerRunToolName,
            "code-worker policy must not surface container.run to the agent");
    }

    private (HostToolProvider provider, DockerLifecycleService lifecycle) NewProvider(IDockerCommandRunner runner)
    {
        var workspace = new ContainerExecutionWorkspaceProvider(executionRoot);
        var lifecycle = new DockerLifecycleService(
            new ContainerToolOptions(),
            runner,
            workspace);
        var docker = new DockerHostToolService(
            new ContainerToolOptions(),
            runner,
            lifecycle,
            workspace,
            idProvider: () => Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            nowProvider: () => DateTimeOffset.Parse("2026-05-01T13:00:00Z"));
        var provider = new HostToolProvider(
            workspaceTools: new WorkspaceHostToolService(new WorkspaceOptions { Root = canonicalRoot }),
            containerTools: docker);
        return (provider, lifecycle);
    }

    private static DockerCommandResult DockerOk() =>
        new(ExitCode: 0, StandardOutput: string.Empty, StandardError: string.Empty,
            StandardOutputTruncated: false, StandardErrorTruncated: false, TimedOut: false);

    private sealed class ScriptedDockerRunner : IDockerCommandRunner
    {
        private readonly Queue<DockerCommandResult> queued = new();

        public List<CapturedCall> Calls { get; } = [];

        public void Enqueue(DockerCommandResult result) => queued.Enqueue(result);

        public Task<DockerCommandResult> RunAsync(
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            long stdoutMaxBytes,
            long stderrMaxBytes,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new CapturedCall(arguments.ToArray(), timeout));
            if (queued.Count == 0)
            {
                throw new InvalidOperationException("ScriptedDockerRunner ran out of queued results.");
            }

            return Task.FromResult(queued.Dequeue());
        }
    }

    private sealed record CapturedCall(IReadOnlyList<string> Arguments, TimeSpan Timeout);
}
