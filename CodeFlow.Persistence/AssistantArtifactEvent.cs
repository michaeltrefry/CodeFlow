namespace CodeFlow.Persistence;

/// <summary>
/// Kinds of artifacts the assistant can produce. Numeric values are STABLE — the column
/// stores the int code, so reordering / removing values would corrupt existing rows.
/// Adding new values is safe; producers in Phase 3 register new kinds without touching
/// existing ones.
/// <para/>
/// sc-799 (AA-8): values 3 and 4 are reserved for AA-9's producers so the contract is
/// locked here, even though the producers haven't shipped yet.
/// <para/>
/// sc-834 (AP-3): values 5 and 6 are agent-package siblings of values 1 and 2. The
/// recorder + repository are kind-agnostic (supersession keys on (conversation, name);
/// expiration keys on snapshotId), so the existing infrastructure handles agent kinds
/// without code changes — the AP-4 producer tools and AP-8 rail UI consume them by enum.
/// <list type="bullet">
///   <item><c>WorkflowPackageDraft = 1</c> — live draft of a workflow package; one active
///     row per (conversation, name). Set / patch replace via supersession.
///     <c>relativePath</c> convention: <c>draft.cf-workflow-package.json</c>.</item>
///   <item><c>WorkflowPackageSnapshot = 2</c> — immutable per-save snapshot of a draft;
///     <c>name</c> uses a GUID suffix so multiple coexist. Apply marks the row expired.
///     <c>relativePath</c> convention: <c>snapshot-{guid:N}.cf-workflow-package.json</c>.</item>
///   <item><c>TraceDiagnostic = 3</c> — JSON summary written by the
///     <c>diagnose_trace</c> tool (AA-9). Live; no supersession.
///     <c>relativePath</c> convention: <c>diagnose-{traceId:N}-{utcTimestamp}.json</c>.</item>
///   <item><c>EvidenceBundle = 4</c> — zipped trace evidence bundle exported by AA-9.
///     Live; no supersession.
///     <c>relativePath</c> convention: <c>evidence-{traceId:N}-{utcTimestamp}.zip</c>.</item>
///   <item><c>AgentPackageDraft = 5</c> — live draft of an agent package; one active row
///     per (conversation, name). Set / patch replace via supersession. Agent and workflow
///     drafts coexist on a conversation by using distinct names.
///     <c>relativePath</c> convention: <c>draft.cf-agent-package.json</c>.</item>
///   <item><c>AgentPackageSnapshot = 6</c> — immutable per-save snapshot of an agent
///     package draft; <c>name</c> uses a GUID suffix so multiple coexist. Apply marks the
///     row expired.
///     <c>relativePath</c> convention: <c>snapshot-{guid:N}.cf-agent-package.json</c>.</item>
/// </list>
/// </summary>
public enum ArtifactEventKind
{
    WorkflowPackageDraft = 1,
    WorkflowPackageSnapshot = 2,
    /// <summary>sc-799 (AA-8): reserved for AA-9's <c>diagnose_trace</c> producer.</summary>
    TraceDiagnostic = 3,
    /// <summary>sc-799 (AA-8): reserved for AA-9's evidence-bundle producer.</summary>
    EvidenceBundle = 4,
    /// <summary>sc-834 (AP-3): live draft of an agent package. Reserved for AP-4's
    /// <c>set_agent_package_draft</c> / <c>patch_agent_package_draft</c> producers.</summary>
    AgentPackageDraft = 5,
    /// <summary>sc-834 (AP-3): immutable per-save snapshot of an agent package draft.
    /// Reserved for AP-4's <c>save_agent_package</c> producer.</summary>
    AgentPackageSnapshot = 6,
}

/// <summary>
/// Domain projection of <see cref="AssistantArtifactEventEntity"/>. Persisted metadata only —
/// the bytes the artifact references live on disk in the conversation workspace at
/// <see cref="RelativePath"/>.
/// </summary>
/// <param name="MessageId">Bound to the assistant message that produced the event when the
/// turn ends; null while the turn is still in flight (or for events produced outside an
/// assistant turn, e.g. by the apply endpoint).</param>
/// <param name="Sequence">Monotonic per conversation. Drives inline-pill ordering.</param>
/// <param name="SnapshotId">Non-null for immutable per-save snapshots; null for drafts.</param>
/// <param name="SummaryJson">Tool-supplied summary (e.g. entry point, item counts). Free-form
/// JSON object; the chat panel reads structured fields from it but is resilient to missing
/// keys.</param>
/// <param name="SupersededByEventId">Set when a later event for the same (conversation, name)
/// replaces this one. UI renders superseded events muted.</param>
/// <param name="ExpiredAtUtc">Set when the underlying file has been consumed (e.g. snapshot
/// deleted by apply) but the event row stays for audit/listing. Download returns 410 Gone
/// when expired.</param>
public sealed record AssistantArtifactEvent(
    Guid Id,
    Guid ConversationId,
    Guid? MessageId,
    int Sequence,
    ArtifactEventKind Kind,
    string Name,
    string RelativePath,
    Guid? SnapshotId,
    string? SummaryJson,
    Guid? SupersededByEventId,
    DateTime? ExpiredAtUtc,
    DateTime CreatedAtUtc);
