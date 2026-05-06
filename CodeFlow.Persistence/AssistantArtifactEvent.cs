namespace CodeFlow.Persistence;

/// <summary>
/// Kinds of artifacts the assistant can produce. Lock the enum values via <see cref="int"/>
/// conversion so the column stores a stable numeric code regardless of how the enum is
/// reordered or extended in source. Values: 1 = workflow-package draft, 2 = workflow-package
/// snapshot. Reserved for Phase 3: 3 = trace diagnostic, 4 = evidence bundle.
/// </summary>
public enum ArtifactEventKind
{
    WorkflowPackageDraft = 1,
    WorkflowPackageSnapshot = 2,
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
