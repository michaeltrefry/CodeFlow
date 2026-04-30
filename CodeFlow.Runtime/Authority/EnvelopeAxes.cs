namespace CodeFlow.Runtime.Authority;

/// <summary>
/// Axis types for <see cref="WorkflowExecutionEnvelope"/>. Each is intersected independently
/// by <see cref="EnvelopeIntersection"/>; the most-restrictive tier wins per axis.
/// </summary>
public sealed record RepoScopeGrant(
    string RepoIdentityKey,
    string Path,
    RepoAccess Access);

public enum RepoAccess
{
    Read,
    Write
}

/// <summary>
/// A grant for a single tool by name and category. Categories are kept as strings so the
/// envelope contract does not reach into <see cref="ToolCategory"/> from
/// <see cref="ToolAccessPolicy"/>; the resolver maps from the persistence
/// <c>AgentRoleToolCategory</c> enum to these strings.
/// </summary>
public sealed record ToolGrant(string ToolName, string Category)
{
    public const string CategoryHost = "Host";
    public const string CategoryMcp = "Mcp";
}

/// <summary>
/// Permission to invoke a specific command via the workspace <c>run_command</c> tool.
/// Pairs with sc-270's <c>WorkspaceOptions.CommandAllowlist</c> — the envelope-resolved value
/// becomes the per-run allowlist when sc-269's tool-layer follow-up threads it through.
/// </summary>
public sealed record ExecuteGrant(string Command, string? Reason = null);

public enum NetworkPolicy
{
    None = 0,
    Loopback = 1,
    Allowlist = 2
}

public sealed record EnvelopeNetwork(
    NetworkPolicy Allow,
    IReadOnlyList<string>? AllowedHosts = null)
{
    public static EnvelopeNetwork Permissive { get; } = new(NetworkPolicy.Allowlist, AllowedHosts: new[] { "*" });

    public static EnvelopeNetwork Denied { get; } = new(NetworkPolicy.None);
}

public sealed record EnvelopeBudget(
    long? MaxTokens = null,
    int? TimeoutSeconds = null,
    int? MaxRepairLoops = null);

public sealed record EnvelopeWorkspace(
    WorkspaceSymlinkPolicy SymlinkPolicy,
    IReadOnlyList<string>? CommandAllowlist = null,
    bool AllowDirty = false);

/// <summary>
/// Mirrors sc-270's enum without taking a project reference on the workspace concrete types.
/// Resolver translation happens at the boundary.
/// </summary>
public enum WorkspaceSymlinkPolicy
{
    AllowAll = 0,
    RefuseForMutation = 1
}

public sealed record DeliveryTarget(
    string Owner,
    string Repo,
    string BaseBranch);
