namespace CodeFlow.Persistence;

/// <summary>
/// Persistence surface for assistant-produced artifact events. Bytes stay on disk in the
/// per-conversation workspace; this repository owns only the metadata rows. See
/// <see cref="AssistantArtifactEvent"/> for the field semantics and
/// <c>docs/assistant-artifacts.md</c> for the design.
/// </summary>
public interface IAssistantArtifactRepository
{
    /// <summary>
    /// Append a new event. The repository assigns the next monotonic <see cref="AssistantArtifactEvent.Sequence"/>
    /// per conversation and bumps the conversation's <c>UpdatedAtUtc</c>.
    /// </summary>
    Task<AssistantArtifactEvent> AddAsync(
        Guid conversationId,
        ArtifactEventKind kind,
        string name,
        string relativePath,
        Guid? snapshotId,
        string? summaryJson,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the conversation's events in <see cref="AssistantArtifactEvent.Sequence"/> order
    /// (oldest first). Includes superseded and expired events — UI is responsible for filtering;
    /// audit / lineage queries need the full set.
    /// </summary>
    Task<IReadOnlyList<AssistantArtifactEvent>> ListByConversationAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default);

    Task<AssistantArtifactEvent?> GetAsync(
        Guid eventId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks every active event with the given <paramref name="name"/> superseded by the given
    /// <paramref name="supersedingEventId"/>. "Active" = not already superseded and not expired.
    /// Idempotent: a second call with the same arguments is a no-op. Used for draft replacement
    /// (set / patch / clear) where the new event takes the spot of the prior draft for that name.
    /// </summary>
    Task<int> MarkActiveSupersededByNameAsync(
        Guid conversationId,
        string name,
        Guid supersedingEventId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks the event keyed by snapshot id as expired (file consumed). The row stays for
    /// audit/listing; the download endpoint surfaces it as gone. Returns the number of rows
    /// updated (0 if no event references the snapshot, otherwise 1).
    /// </summary>
    Task<int> MarkExpiredBySnapshotIdAsync(
        Guid snapshotId,
        DateTime expiredAtUtc,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Late-bind a previously-recorded event to the assistant message that produced it. Called
    /// at the end of the streaming turn once the assistant message has been persisted and has
    /// a real id. Idempotent.
    /// </summary>
    Task BindMessageAsync(
        Guid eventId,
        Guid messageId,
        CancellationToken cancellationToken = default);
}
