namespace CodeFlow.Persistence.Authority;

/// <summary>
/// Per-agent-invocation authority snapshot — the resolved <see cref="CodeFlow.Runtime.Authority.WorkflowExecutionEnvelope"/>
/// captured at the moment the invocation was admitted, plus any per-axis denial evidence.
/// Operators can answer "what was this invocation authorized to do, and which tier blocked
/// what?" without re-running the resolver. See sc-269 PR2.
/// </summary>
public sealed class AgentInvocationAuthorityEntity
{
    public Guid Id { get; set; }

    public Guid TraceId { get; set; }

    public Guid RoundId { get; set; }

    public string AgentKey { get; set; } = string.Empty;

    public int? AgentVersion { get; set; }

    public string? WorkflowKey { get; set; }

    public int? WorkflowVersion { get; set; }

    /// <summary>
    /// Resolved <see cref="CodeFlow.Runtime.Authority.WorkflowExecutionEnvelope"/> serialized
    /// as JSON. Read-only — sized for fast inspector loads, not for use as the source of
    /// truth at enforcement time.
    /// </summary>
    public string EnvelopeJson { get; set; } = string.Empty;

    /// <summary>
    /// Per-axis <see cref="CodeFlow.Runtime.Authority.BlockedBy"/> records emitted by
    /// intersection, serialized as a JSON array. Empty array when nothing was blocked.
    /// </summary>
    public string BlockedAxesJson { get; set; } = "[]";

    /// <summary>
    /// Source tiers consulted by intersection (name + envelope), serialized as a JSON array.
    /// Preserves the per-tier evidence so a future inspector can show "which tier said what."
    /// </summary>
    public string TiersJson { get; set; } = "[]";

    public DateTime ResolvedAtUtc { get; set; }
}
