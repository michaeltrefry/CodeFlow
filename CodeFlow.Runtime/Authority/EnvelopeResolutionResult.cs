namespace CodeFlow.Runtime.Authority;

/// <summary>
/// Output of <see cref="EnvelopeIntersection.Intersect"/>: the resolved envelope plus any
/// per-axis denials and the source tiers consulted. <see cref="BlockedAxes"/> drives the
/// <see cref="RefusalEvent"/> emissions sc-285 plumbed; <see cref="Tiers"/> is preserved
/// alongside the saga so an operator can answer "which tier blocked this?" without
/// re-running the resolver.
/// </summary>
public sealed record EnvelopeResolutionResult(
    WorkflowExecutionEnvelope Envelope,
    IReadOnlyList<BlockedBy> BlockedAxes,
    IReadOnlyList<EnvelopeTier> Tiers);

/// <summary>
/// Per-axis denial evidence emitted by intersection. One <c>BlockedBy</c> per (tier, axis)
/// pair where one tier removed something another tier granted, or where a structural
/// invariant was violated (mismatched delivery target, etc.).
/// </summary>
public sealed record BlockedBy(
    string Tier,
    string Axis,
    string Code,
    string Reason,
    string? RequestedValue = null,
    string? AllowedValue = null)
{
    public static class Axes
    {
        public const string RepoScopes = "repoScopes";
        public const string ToolGrants = "toolGrants";
        public const string ExecuteGrants = "executeGrants";
        public const string Network = "network";
        public const string Budget = "budget";
        public const string Workspace = "workspace";
        public const string Delivery = "delivery";
    }

    public static class Codes
    {
        /// <summary>A tier removed an item that another tier granted (set-intersection emptied).</summary>
        public const string TierRemoved = "tier-removed";

        /// <summary>No tier expressed any value for this axis; resolved value is the seed default.</summary>
        public const string NoOverlap = "no-overlap";

        /// <summary>Tiers expressed conflicting values for an axis that requires exact agreement (e.g., delivery target).</summary>
        public const string Conflict = "conflict";

        /// <summary>A tier narrowed a numeric budget or string-set restriction below another tier's request.</summary>
        public const string Narrowed = "narrowed";
    }
}
