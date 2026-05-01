using CodeFlow.Runtime.Container;
using FluentAssertions;

namespace CodeFlow.Runtime.Tests.Container;

public sealed class DockerLifecycleServiceTests
{
    [Fact]
    public async Task CleanupWorkflowAsync_removes_labeled_containers_and_volumes()
    {
        var workflowId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var runner = new QueueingDockerRunner([
            new DockerCommandResult(0, "container-a\ncontainer-b\n", string.Empty, false, false, TimedOut: false),
            new DockerCommandResult(0, "removed containers", string.Empty, false, false, TimedOut: false),
            new DockerCommandResult(0, "volume-a\n", string.Empty, false, false, TimedOut: false),
            new DockerCommandResult(0, "removed volumes", string.Empty, false, false, TimedOut: false)
        ]);
        var service = new DockerLifecycleService(new ContainerToolOptions(), runner);

        var result = await service.CleanupWorkflowAsync(workflowId);

        result.RemovedContainers.Should().Be(2);
        result.RemovedVolumes.Should().Be(1);
        runner.Calls.Should().HaveCount(4);
        runner.Calls[0].Arguments.Should().ContainInOrder(
            "ps",
            "-aq",
            "--filter",
            "label=codeflow.managed=true",
            "--filter",
            "label=codeflow.workflow=11111111222233334444555555555555");
        runner.Calls[1].Arguments.Should().Equal("rm", "-f", "container-a", "container-b");
        runner.Calls[2].Arguments.Should().ContainInOrder(
            "volume",
            "ls",
            "-q",
            "--filter",
            "label=codeflow.managed=true",
            "--filter",
            "label=codeflow.workflow=11111111222233334444555555555555");
        runner.Calls[3].Arguments.Should().Equal("volume", "rm", "-f", "volume-a");
    }

    [Fact]
    public async Task CleanupWorkflowAsync_skips_remove_when_no_resources_found()
    {
        var runner = new QueueingDockerRunner([
            new DockerCommandResult(0, string.Empty, string.Empty, false, false, TimedOut: false),
            new DockerCommandResult(0, string.Empty, string.Empty, false, false, TimedOut: false)
        ]);
        var service = new DockerLifecycleService(new ContainerToolOptions(), runner);

        var result = await service.CleanupWorkflowAsync(Guid.NewGuid());

        result.RemovedContainers.Should().Be(0);
        result.RemovedVolumes.Should().Be(0);
        runner.Calls.Should().HaveCount(2);
        runner.Calls.Should().OnlyContain(call => call.Arguments.Contains("ls") || call.Arguments.Contains("ps"));
    }

    [Fact]
    public async Task SweepOrphansAsync_removes_only_resources_older_than_ttl()
    {
        var now = DateTimeOffset.Parse("2026-05-01T13:00:00Z");
        var runner = new QueueingDockerRunner([
            new DockerCommandResult(
                0,
                "old-container\t2026-04-30T12:00:00Z\nfresh-container\t2026-05-01T12:30:00Z\nunknown-container\t\n",
                string.Empty,
                false,
                false,
                TimedOut: false),
            new DockerCommandResult(0, "removed containers", string.Empty, false, false, TimedOut: false),
            new DockerCommandResult(
                0,
                "old-volume\t2026-04-30T12:00:00Z\nfresh-volume\t2026-05-01T12:30:00Z\n",
                string.Empty,
                false,
                false,
                TimedOut: false),
            new DockerCommandResult(0, "removed volumes", string.Empty, false, false, TimedOut: false)
        ]);
        var options = new ContainerToolOptions { OrphanCleanupTtl = TimeSpan.FromHours(24) };
        var service = new DockerLifecycleService(options, runner, () => now);

        var result = await service.SweepOrphansAsync();

        result.RemovedContainers.Should().Be(1);
        result.RemovedVolumes.Should().Be(1);
        runner.Calls.Should().HaveCount(4);
        runner.Calls[1].Arguments.Should().Equal("rm", "-f", "old-container");
        runner.Calls[3].Arguments.Should().Equal("volume", "rm", "-f", "old-volume");
    }

    [Fact]
    public async Task CountWorkflowContainersAsync_returns_labeled_container_count()
    {
        var runner = new QueueingDockerRunner([
            new DockerCommandResult(0, "a\nb\n", string.Empty, false, false, TimedOut: false)
        ]);
        var service = new DockerLifecycleService(new ContainerToolOptions(), runner);

        var count = await service.CountWorkflowContainersAsync(Guid.NewGuid());

        count.Should().Be(2);
    }

    private sealed class QueueingDockerRunner : IDockerCommandRunner
    {
        private readonly Queue<DockerCommandResult> results;

        public QueueingDockerRunner(IEnumerable<DockerCommandResult> results)
        {
            this.results = new Queue<DockerCommandResult>(results);
        }

        public List<CapturedDockerCall> Calls { get; } = [];

        public Task<DockerCommandResult> RunAsync(
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            long stdoutMaxBytes,
            long stderrMaxBytes,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new CapturedDockerCall(arguments.ToArray(), timeout));
            if (results.Count == 0)
            {
                throw new InvalidOperationException("No queued Docker result.");
            }

            return Task.FromResult(results.Dequeue());
        }
    }

    private sealed record CapturedDockerCall(IReadOnlyList<string> Arguments, TimeSpan Timeout);
}
