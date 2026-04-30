namespace CodeFlow.Runtime.Authority;

/// <summary>
/// Resolved capability envelope / authority snapshot for a single workflow run or agent
/// invocation. One bundled answer to "what is this run authorized to do" rather than a
/// scattered set of role-grant lookups, repo-scope checks, network policy reads, and
/// budget cap reads.
///
/// Produced by <see cref="IAuthorityResolver"/> via <see cref="EnvelopeIntersection"/>, which
/// intersects per-tier envelopes (tenant → workflow → role → trace context) and produces
/// the most-restrictive resolved value per axis along with <see cref="BlockedBy"/> evidence
/// for any axes that were denied. See sc-269.
///
/// **Nullable axes are intentional.** Each axis can be:
/// <list type="bullet">
///   <item><description><c>null</c> — this tier has no opinion. Other tiers' values pass through.</description></item>
///   <item><description>Empty list / restrictive scalar — this tier explicitly denies. Intersection narrows accordingly.</description></item>
///   <item><description>Non-empty list / permissive scalar — this tier grants what's listed.</description></item>
/// </list>
/// Without this distinction "tenant tier is silent" and "tenant tier denies everything"
/// collapse to the same shape and intersection becomes meaningless in single-tenant
/// deployments where most tiers don't yet contribute.
///
/// Designed to be mintable via the resolver, not constructed directly by consumers — pairs
/// with sc-272's admitted-handoff types when those land. The record is freely constructible
/// for v1 to keep this PR small; the validator/minter pattern follows in sc-272.
/// </summary>
public sealed record WorkflowExecutionEnvelope(
    IReadOnlyList<RepoScopeGrant>? RepoScopes,
    IReadOnlyList<ToolGrant>? ToolGrants,
    IReadOnlyList<ExecuteGrant>? ExecuteGrants,
    EnvelopeNetwork? Network,
    EnvelopeBudget? Budget,
    EnvelopeWorkspace? Workspace,
    DeliveryTarget? Delivery)
{
    /// <summary>
    /// "No opinion on any axis" — used as the seed for intersection and as a sensible default
    /// for tiers that don't yet contribute (e.g., the tenant tier in single-tenant
    /// deployments). All axes are <c>null</c>; intersection passes other tiers' values through.
    /// </summary>
    public static WorkflowExecutionEnvelope NoOpinion { get; } = new(
        RepoScopes: null,
        ToolGrants: null,
        ExecuteGrants: null,
        Network: null,
        Budget: null,
        Workspace: null,
        Delivery: null);
}

/// <summary>
/// One tier's contribution to envelope resolution. Tiers are passed to
/// <see cref="EnvelopeIntersection.Intersect"/> in precedence order (lower index = broader
/// scope). Standard tier names are exposed on <see cref="Tiers"/> for telemetry consistency.
/// </summary>
public sealed record EnvelopeTier(string Name, WorkflowExecutionEnvelope Envelope);

public static class Tiers
{
    public const string Tenant = "tenant";
    public const string Workflow = "workflow";
    public const string Role = "role";
    public const string Context = "context";
}
