using System.Globalization;

namespace CodeFlow.Runtime.Container;

public sealed class DockerLifecycleService
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromMinutes(5);

    private readonly ContainerToolOptions options;
    private readonly IDockerCommandRunner runner;
    private readonly ContainerExecutionWorkspaceProvider? executionWorkspaces;
    private readonly Func<DateTimeOffset> nowProvider;

    public DockerLifecycleService(
        ContainerToolOptions? options = null,
        IDockerCommandRunner? runner = null,
        ContainerExecutionWorkspaceProvider? executionWorkspaces = null,
        Func<DateTimeOffset>? nowProvider = null)
    {
        this.options = options ?? new ContainerToolOptions();
        var errors = this.options.Validate();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException("Container tool options are invalid: " + string.Join(" ", errors));
        }

        this.runner = runner ?? new DockerCliCommandRunner();
        this.executionWorkspaces = executionWorkspaces;
        this.nowProvider = nowProvider ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<DockerWorkflowCleanupResult> CleanupWorkflowAsync(
        Guid workflowId,
        CancellationToken cancellationToken = default)
    {
        var workflowLabel = BuildLabelFilter(DockerResourceLabels.Workflow, workflowId.ToString("N"));
        var containers = await ListResourceNamesAsync(
            "ps",
            [
                "-aq",
                "--filter",
                BuildLabelFilter(DockerResourceLabels.Managed, DockerResourceLabels.ManagedValue),
                "--filter",
                workflowLabel
            ],
            cancellationToken);

        var removedContainers = await RemoveResourcesAsync("rm", ["-f"], containers, cancellationToken);

        var volumes = await ListResourceNamesAsync(
            "volume",
            [
                "ls",
                "-q",
                "--filter",
                BuildLabelFilter(DockerResourceLabels.Managed, DockerResourceLabels.ManagedValue),
                "--filter",
                workflowLabel
            ],
            cancellationToken);

        var removedVolumes = await RemoveResourcesAsync("volume", ["rm", "-f"], volumes, cancellationToken);

        var removedExecutionWorkspaces = (executionWorkspaces?.RemoveWorkflow(workflowId) ?? false) ? 1 : 0;

        return new DockerWorkflowCleanupResult(removedContainers, removedVolumes, removedExecutionWorkspaces);
    }

    public async Task<DockerWorkflowCleanupResult> SweepOrphansAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = nowProvider() - options.OrphanCleanupTtl;
        var containers = await ListResourcesWithCreatedAtAsync(
            "ps",
            [
                "-a",
                "--filter",
                BuildLabelFilter(DockerResourceLabels.Managed, DockerResourceLabels.ManagedValue),
                "--format",
                $"{{{{.ID}}}}\t{{{{.Label \"{DockerResourceLabels.CreatedAt}\"}}}}"
            ],
            cutoff,
            cancellationToken);

        var removedContainers = await RemoveResourcesAsync("rm", ["-f"], containers, cancellationToken);

        var volumes = await ListResourcesWithCreatedAtAsync(
            "volume",
            [
                "ls",
                "--filter",
                BuildLabelFilter(DockerResourceLabels.Managed, DockerResourceLabels.ManagedValue),
                "--format",
                $"{{{{.Name}}}}\t{{{{.Label \"{DockerResourceLabels.CreatedAt}\"}}}}"
            ],
            cutoff,
            cancellationToken);

        var removedVolumes = await RemoveResourcesAsync("volume", ["rm", "-f"], volumes, cancellationToken);

        var removedExecutionWorkspaces = executionWorkspaces?.SweepOrphans(options.OrphanCleanupTtl, nowProvider()) ?? 0;

        return new DockerWorkflowCleanupResult(removedContainers, removedVolumes, removedExecutionWorkspaces);
    }

    public async Task<int> CountWorkflowContainersAsync(
        Guid workflowId,
        CancellationToken cancellationToken = default)
    {
        var containers = await ListResourceNamesAsync(
            "ps",
            [
                "-aq",
                "--filter",
                BuildLabelFilter(DockerResourceLabels.Managed, DockerResourceLabels.ManagedValue),
                "--filter",
                BuildLabelFilter(DockerResourceLabels.Workflow, workflowId.ToString("N"))
            ],
            cancellationToken);

        return containers.Count;
    }

    private async Task<IReadOnlyList<string>> ListResourceNamesAsync(
        string dockerObject,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(
            [dockerObject, .. arguments],
            CleanupTimeout,
            options.StdoutMaxBytes,
            options.StderrMaxBytes,
            cancellationToken);

        if (result.ExitCode != 0 || result.TimedOut)
        {
            throw new InvalidOperationException(
                $"Docker {dockerObject} list failed during container cleanup: {result.StandardError}");
        }

        return SplitNonEmptyLines(result.StandardOutput);
    }

    private async Task<IReadOnlyList<string>> ListResourcesWithCreatedAtAsync(
        string dockerObject,
        IReadOnlyList<string> arguments,
        DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(
            [dockerObject, .. arguments],
            CleanupTimeout,
            options.StdoutMaxBytes,
            options.StderrMaxBytes,
            cancellationToken);

        if (result.ExitCode != 0 || result.TimedOut)
        {
            throw new InvalidOperationException(
                $"Docker {dockerObject} list failed during orphan sweep: {result.StandardError}");
        }

        return SplitNonEmptyLines(result.StandardOutput)
            .Select(ParseResourceWithCreatedAt)
            .Where(resource => resource.CreatedAt is not null && resource.CreatedAt.Value < cutoff)
            .Select(static resource => resource.Name)
            .ToArray();
    }

    private async Task<int> RemoveResourcesAsync(
        string dockerObject,
        IReadOnlyList<string> removeArguments,
        IReadOnlyList<string> resourceNames,
        CancellationToken cancellationToken)
    {
        if (resourceNames.Count == 0)
        {
            return 0;
        }

        var result = await runner.RunAsync(
            [dockerObject, .. removeArguments, .. resourceNames],
            CleanupTimeout,
            options.StdoutMaxBytes,
            options.StderrMaxBytes,
            cancellationToken);

        if (result.ExitCode != 0 || result.TimedOut)
        {
            throw new InvalidOperationException(
                $"Docker {dockerObject} remove failed during container cleanup: {result.StandardError}");
        }

        return resourceNames.Count;
    }

    private static DockerResourceWithCreatedAt ParseResourceWithCreatedAt(string line)
    {
        var columns = line.Split('\t', count: 2);
        var name = columns[0].Trim();
        if (columns.Length < 2
            || !DateTimeOffset.TryParse(
                columns[1].Trim(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var createdAt))
        {
            return new DockerResourceWithCreatedAt(name, CreatedAt: null);
        }

        return new DockerResourceWithCreatedAt(name, createdAt);
    }

    private static IReadOnlyList<string> SplitNonEmptyLines(string value) =>
        value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string BuildLabelFilter(string name, string value) => $"label={name}={value}";

    private sealed record DockerResourceWithCreatedAt(string Name, DateTimeOffset? CreatedAt);
}

public sealed record DockerWorkflowCleanupResult(
    int RemovedContainers,
    int RemovedVolumes,
    int RemovedExecutionWorkspaces = 0);
