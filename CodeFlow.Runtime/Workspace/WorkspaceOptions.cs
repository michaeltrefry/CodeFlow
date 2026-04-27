namespace CodeFlow.Runtime.Workspace;

public sealed class WorkspaceOptions
{
    public const string SectionName = "Workspace";

    public const string CacheDirectoryName = "cache";

    public const string WorkDirectoryName = "work";

    /// <summary>
    /// Default container path for per-trace working directories used by code-aware workflows.
    /// Both api and worker containers mount this same path as a shared volume; the api creates
    /// <c>{root}/{traceId:N}/</c> on trace start, and the worker reads/writes through it via
    /// path-jailed host tools (<c>read_file</c>, <c>apply_patch</c>, <c>run_command</c>).
    /// Override via <c>Workspace__WorkingDirectoryRoot</c> only when running outside the standard
    /// container layout (e.g. integration tests pointing at a per-test temp dir).
    /// </summary>
    public const string DefaultWorkingDirectoryRoot = "/app/codeflow/workdir";

    public string Root { get; set; } = string.Empty;

    /// <summary>
    /// Per-trace working-directory root for code-aware workflows. Defaults to
    /// <see cref="DefaultWorkingDirectoryRoot"/> so a stock deployment requires no admin config;
    /// the path is intentionally not editable via the admin UI because it must match a host-side
    /// volume mount that the operator manages out-of-band.
    /// </summary>
    public string WorkingDirectoryRoot { get; set; } = DefaultWorkingDirectoryRoot;

    public TimeSpan WorktreeTtl { get; set; } = TimeSpan.FromHours(24);

    public long ReadMaxBytes { get; set; } = 512 * 1024;

    public int ExecTimeoutSeconds { get; set; } = 600;

    public long ExecOutputMaxBytes { get; set; } = 1024 * 1024;

    public TimeSpan GitCommandTimeout { get; set; } = TimeSpan.FromMinutes(10);

    public string CachePath => Path.Combine(Root, CacheDirectoryName);

    public string WorkPath => Path.Combine(Root, WorkDirectoryName);
}
