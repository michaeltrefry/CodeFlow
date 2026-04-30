namespace CodeFlow.Persistence.Replay;

/// <summary>
/// sc-275 — append-only record of a replay-with-edit attempt. Replay execution itself is
/// ephemeral (no new saga rows are created), so lineage information has to live on the
/// replay POST itself rather than be reconstructed from saga state. Each row carries
/// enough context to render an inspectable lineage tree per parent trace and to surface
/// in trace evidence bundles.
///
/// Generation is always 1 in v1 (replays of an original saga). The schema leaves room
/// for a `parent_lineage_id` column + replay-of-replay chaining as a future fast-follow
/// once there's a real use case for gen-2+ chains.
/// </summary>
public sealed class ReplayAttemptEntity
{
    public Guid Id { get; set; }

    /// <summary>The recorded saga's trace id that the replay was rooted at.</summary>
    public Guid ParentTraceId { get; set; }

    /// <summary>
    /// Stable identifier shared by every replay attempt with identical inputs against the
    /// same parent. Computed as a Guid derived from <c>SHA-256(parent_trace_id ‖ content_hash)</c>.
    /// </summary>
    public Guid LineageId { get; set; }

    /// <summary>
    /// SHA-256 (lowercase hex, 64 chars) over a canonical JSON form of the replay request:
    /// edits sorted by (agentKey, ordinal), additional mocks sorted by agentKey, workflow
    /// version override, pinned agent versions sorted by key. Stable across runs with the
    /// same inputs.
    /// </summary>
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>
    /// 1 for direct replays of an original saga. Reserved for future chaining; the
    /// hasher + endpoint don't compute &gt;1 today.
    /// </summary>
    public int Generation { get; set; } = 1;

    /// <summary>Terminal state returned by the dry-run executor.</summary>
    public string ReplayState { get; set; } = string.Empty;

    public string? TerminalPort { get; set; }

    /// <summary>None / Soft / Hard from <c>ReplayDriftDetector</c>.</summary>
    public string DriftLevel { get; set; } = "None";

    /// <summary>Caller-supplied source identifier (e.g. <c>ui:replay-panel</c>).</summary>
    public string? Reason { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
