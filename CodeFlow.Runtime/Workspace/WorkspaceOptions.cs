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

    /// <summary>
    /// Default container path for per-conversation working directories used by the homepage
    /// assistant when an agent role is assigned (so its host tools — read_file, apply_patch,
    /// run_command — have a workspace to operate against). Each conversation gets
    /// <c>{root}/{conversationId:N}/</c> on first tool call. Override via
    /// <c>Workspace__AssistantWorkspaceRoot</c> for non-standard layouts.
    /// </summary>
    public const string DefaultAssistantWorkspaceRoot = "/app/codeflow/assistant";

    public string Root { get; set; } = string.Empty;

    /// <summary>
    /// Per-trace working-directory root for code-aware workflows. Defaults to
    /// <see cref="DefaultWorkingDirectoryRoot"/> so a stock deployment requires no admin config;
    /// the path is intentionally not editable via the admin UI because it must match a host-side
    /// volume mount that the operator manages out-of-band.
    /// </summary>
    public string WorkingDirectoryRoot { get; set; } = DefaultWorkingDirectoryRoot;

    /// <summary>
    /// Per-conversation working-directory root for the homepage assistant. Same out-of-band
    /// volume-mount considerations as <see cref="WorkingDirectoryRoot"/>; not editable via the
    /// admin UI for the same reason.
    /// </summary>
    public string AssistantWorkspaceRoot { get; set; } = DefaultAssistantWorkspaceRoot;

    public TimeSpan WorktreeTtl { get; set; } = TimeSpan.FromHours(24);

    public long ReadMaxBytes { get; set; } = 512 * 1024;

    public int ExecTimeoutSeconds { get; set; } = 600;

    public long ExecOutputMaxBytes { get; set; } = 1024 * 1024;

    public TimeSpan GitCommandTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Allowlist of process names <c>run_command</c> may invoke. <c>null</c> or empty means no
    /// allowlist enforcement (back-compat default). Names are matched case-insensitively against
    /// the <c>command</c> argument's basename without extension on Windows, and exactly on Unix.
    /// </summary>
    public IList<string>? CommandAllowlist { get; set; }

    /// <summary>
    /// Policy for symlink targets encountered during workspace mutation
    /// (<c>apply_patch</c>'s Add/Update/Delete). <see cref="WorkspaceSymlinkPolicy.RefuseForMutation"/>
    /// matches Protostar's behavior and is the recommended default; reads remain unaffected.
    /// </summary>
    public WorkspaceSymlinkPolicy SymlinkPolicy { get; set; } = WorkspaceSymlinkPolicy.RefuseForMutation;

    public string CachePath => Path.Combine(Root, CacheDirectoryName);

    public string WorkPath => Path.Combine(Root, WorkDirectoryName);
}

public enum WorkspaceSymlinkPolicy
{
    AllowAll = 0,
    RefuseForMutation = 1
}
