namespace CodeFlow.Runtime.Container;

public sealed class ContainerToolOptions
{
    public const string SectionName = "ContainerTools";

    public IList<string> AllowedImageRegistries { get; set; } = ["docker.io"];

    public bool AllowNetwork { get; set; } = true;

    public string WorkspaceMountPath { get; set; } = "/workspace";

    public string ExecutionWorkspaceDirectoryName { get; set; } = "container-workspace";

    /// <summary>
    /// Absolute host path where per-workflow execution workspaces live (one
    /// <c>{traceId:N}/</c> subdirectory per active workflow). When empty, the host derives a
    /// default from <c>WorkspaceOptions.WorkingDirectoryRoot</c> + <c>..</c> +
    /// <see cref="ExecutionWorkspaceDirectoryName"/> so the canonical workdir-sweep doesn't see
    /// the execution copies as stray children of its own root.
    /// </summary>
    public string? ExecutionWorkspaceRootPath { get; set; }

    public double CpuCount { get; set; } = 2;

    public long MemoryBytes { get; set; } = 4L * 1024 * 1024 * 1024;

    public int PidsLimit { get; set; } = 1024;

    public int CommandTimeoutSeconds { get; set; } = 20 * 60;

    public int ImagePullTimeoutSeconds { get; set; } = 10 * 60;

    public long StdoutMaxBytes { get; set; } = 2 * 1024 * 1024;

    public long StderrMaxBytes { get; set; } = 2 * 1024 * 1024;

    public int MaxContainersPerWorkflow { get; set; } = 3;

    public int MaxCacheVolumesPerWorkflow { get; set; } = 5;

    public TimeSpan OrphanCleanupTtl { get; set; } = TimeSpan.FromHours(24);

    public bool AllowRepoDockerfiles { get; set; }

    public bool AllowDockerBuild { get; set; }

    public bool AllowDockerCompose { get; set; }

    public bool AllowPrivileged { get; set; }

    public bool AllowHostNetwork { get; set; }

    public bool AllowPublishedPorts { get; set; }

    public bool AllowDockerSocketMount { get; set; }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (AllowedImageRegistries is null || AllowedImageRegistries.Count == 0)
        {
            errors.Add("ContainerTools:AllowedImageRegistries must contain at least one registry.");
        }
        else if (AllowedImageRegistries.Any(static registry => string.IsNullOrWhiteSpace(registry)))
        {
            errors.Add("ContainerTools:AllowedImageRegistries must not contain blank entries.");
        }

        if (string.IsNullOrWhiteSpace(WorkspaceMountPath) || !WorkspaceMountPath.StartsWith("/", StringComparison.Ordinal))
        {
            errors.Add("ContainerTools:WorkspaceMountPath must be an absolute container path.");
        }

        if (string.IsNullOrWhiteSpace(ExecutionWorkspaceDirectoryName)
            || ExecutionWorkspaceDirectoryName.Contains('/')
            || ExecutionWorkspaceDirectoryName.Contains('\\'))
        {
            errors.Add("ContainerTools:ExecutionWorkspaceDirectoryName must be a single directory name.");
        }

        if (!string.IsNullOrWhiteSpace(ExecutionWorkspaceRootPath)
            && !Path.IsPathFullyQualified(ExecutionWorkspaceRootPath))
        {
            errors.Add("ContainerTools:ExecutionWorkspaceRootPath must be an absolute host path when set.");
        }

        if (CpuCount <= 0)
        {
            errors.Add("ContainerTools:CpuCount must be greater than zero.");
        }

        if (MemoryBytes <= 0)
        {
            errors.Add("ContainerTools:MemoryBytes must be greater than zero.");
        }

        if (PidsLimit <= 0)
        {
            errors.Add("ContainerTools:PidsLimit must be greater than zero.");
        }

        if (CommandTimeoutSeconds <= 0)
        {
            errors.Add("ContainerTools:CommandTimeoutSeconds must be greater than zero.");
        }

        if (ImagePullTimeoutSeconds <= 0)
        {
            errors.Add("ContainerTools:ImagePullTimeoutSeconds must be greater than zero.");
        }

        if (StdoutMaxBytes <= 0)
        {
            errors.Add("ContainerTools:StdoutMaxBytes must be greater than zero.");
        }

        if (StderrMaxBytes <= 0)
        {
            errors.Add("ContainerTools:StderrMaxBytes must be greater than zero.");
        }

        if (MaxContainersPerWorkflow <= 0)
        {
            errors.Add("ContainerTools:MaxContainersPerWorkflow must be greater than zero.");
        }

        if (MaxCacheVolumesPerWorkflow < 0)
        {
            errors.Add("ContainerTools:MaxCacheVolumesPerWorkflow must be zero or greater.");
        }

        if (OrphanCleanupTtl <= TimeSpan.Zero)
        {
            errors.Add("ContainerTools:OrphanCleanupTtl must be greater than zero.");
        }

        if (AllowRepoDockerfiles)
        {
            errors.Add("ContainerTools:AllowRepoDockerfiles must remain false.");
        }

        if (AllowDockerBuild)
        {
            errors.Add("ContainerTools:AllowDockerBuild must remain false.");
        }

        if (AllowDockerCompose)
        {
            errors.Add("ContainerTools:AllowDockerCompose must remain false.");
        }

        if (AllowPrivileged)
        {
            errors.Add("ContainerTools:AllowPrivileged must remain false.");
        }

        if (AllowHostNetwork)
        {
            errors.Add("ContainerTools:AllowHostNetwork must remain false.");
        }

        if (AllowPublishedPorts)
        {
            errors.Add("ContainerTools:AllowPublishedPorts must remain false.");
        }

        if (AllowDockerSocketMount)
        {
            errors.Add("ContainerTools:AllowDockerSocketMount must remain false.");
        }

        return errors;
    }
}
