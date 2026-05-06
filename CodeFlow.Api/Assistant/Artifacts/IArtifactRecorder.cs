using CodeFlow.Persistence;

namespace CodeFlow.Api.Assistant.Artifacts;

/// <summary>
/// Canonical write path for assistant-produced artifact events. Producer tools (workflow-package
/// draft tools, save snapshot path, future trace-diagnostic / evidence-bundle tools) call this
/// to register a downloadable artifact in the conversation; the recorder owns sequencing,
/// supersession, and the underlying repository write. Bytes stay on disk in the conversation
/// workspace — the recorder is metadata-only.
/// </summary>
/// <remarks>
/// AA-1 introduces the recorder as the package-tool helper. AA-8 (Phase 3) will lock this as
/// the only sanctioned write path; for now <see cref="IAssistantArtifactRepository"/> stays
/// reachable directly to keep the abstraction discovery low-friction.
/// </remarks>
public interface IArtifactRecorder
{
    /// <summary>
    /// Append an artifact event to the conversation. When <paramref name="supersedesPriorByName"/>
    /// is true (typical for drafts that overwrite an earlier version of the same file), every
    /// active prior event with the same <paramref name="name"/> in this conversation is marked
    /// superseded by the new event. Snapshots leave this false — each snapshot is uniquely named
    /// by GUID and immutable; multiple coexist.
    /// </summary>
    Task<AssistantArtifactEvent> RecordAsync(
        Guid conversationId,
        ArtifactEventKind kind,
        string name,
        string relativePath,
        Guid? snapshotId,
        string? summaryJson,
        bool supersedesPriorByName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks every active event with the given <paramref name="name"/> superseded — the
    /// "supersede without replacement" path used by <c>clear_workflow_package_draft</c>.
    /// Returns the number of rows updated. The synthetic superseding-event-id used here is
    /// recorded as a Cleared marker so the UI can render an "x removed the draft" lineage entry
    /// without a fake artifact pill.
    /// </summary>
    Task<int> ClearByNameAsync(
        Guid conversationId,
        string name,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks the event referenced by <paramref name="snapshotId"/> expired (file consumed by
    /// the apply endpoint). Returns the number of rows updated (0 or 1).
    /// </summary>
    Task<int> MarkSnapshotExpiredAsync(
        Guid snapshotId,
        CancellationToken cancellationToken = default);
}
