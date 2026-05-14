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

    /// <summary>
    /// Epic 993 / NO-10: the dispatching node's per-node <see cref="CodeFlow.Contracts.AgentInvocationOverrides"/>
    /// serialized as JSON, or null when the node declared no overrides. Captures what the round
    /// actually ran with — the provider/model/budget/additive-tool overlay — so the trace
    /// inspector can show "this round ran with overrides" without re-deriving it from the
    /// (possibly-since-changed) workflow definition.
    /// </summary>
    public string? AgentOverridesJson { get; set; }

    public DateTime ResolvedAtUtc { get; set; }
}
