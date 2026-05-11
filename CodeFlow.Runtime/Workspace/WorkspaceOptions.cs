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
    public const string DefaultWorkingDirectoryRoot = "/workspace";

    /// <summary>
    /// Default container path for per-conversation working directories used by the homepage
    /// assistant when an agent role is assigned (so its host tools — read_file, apply_patch,
    /// run_command — have a workspace to operate against). Each conversation gets
    /// <c>{root}/{conversationId:N}/</c> on first tool call. Override via
    /// <c>Workspace__AssistantWorkspaceRoot</c> for non-standard layouts.
    /// </summary>
    public const string DefaultAssistantWorkspaceRoot = "/workspace/assistant";

    /// <summary>
    /// Default container path for per-trace git credential files (epic 658). The api writes
    /// <c>{root}/{traceId:N}</c> in git's native credential-store format at trace start; both
    /// api and worker spawn <c>git</c> with <c>GIT_CONFIG_*</c> env vars that point
    /// <c>credential.helper</c> at this file. Lives outside <see cref="DefaultWorkingDirectoryRoot"/>
    /// so the agent's path-confined tools (<c>read_file</c>, <c>run_command</c>) cannot reach it.
    /// </summary>
    public const string DefaultGitCredentialRoot = "/var/lib/codeflow/git-creds";

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

    /// <summary>
    /// Per-trace git credential file root (epic 658). Override via
    /// <c>Workspace__GitCredentialRoot</c> for non-container dev. Same rationale as
    /// <see cref="WorkingDirectoryRoot"/>: not editable via the admin UI because it must match a
    /// host-side mount the operator manages out-of-band, and the agent must never be able to
    /// reach it.
    /// </summary>
    public string GitCredentialRoot { get; set; } = DefaultGitCredentialRoot;

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

    /// <summary>
    /// Hard ceiling on the number of files <c>bulk_replace</c> may scan in a single call.
    /// Refuses with <c>too_many_files</c> rather than half-applying. The number is a comfort
    /// knob, not a safety boundary — the tool always either succeeds against every matched
    /// file or refuses before touching any of them, so raising the cap doesn't introduce a
    /// half-applied-state risk. Sized to cover repo-wide renames on medium-large .NET
    /// projects (CodeFlow itself has ~1000 .cs files after standard exclusions).
    /// </summary>
    public int BulkReplaceMaxFiles { get; set; } = 2000;

    /// <summary>
    /// Per-file regex match timeout for <c>bulk_replace</c> when <c>regex: true</c>. The tool
    /// also pins <c>RegexOptions.NonBacktracking</c> so the timeout is mostly defense-in-depth
    /// against pathological replacement patterns.
    /// </summary>
    public TimeSpan BulkReplaceRegexTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public string CachePath => Path.Combine(Root, CacheDirectoryName);

    public string WorkPath => Path.Combine(Root, WorkDirectoryName);
}

public enum WorkspaceSymlinkPolicy
{
    AllowAll = 0,
    RefuseForMutation = 1
}
