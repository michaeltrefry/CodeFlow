namespace CodeFlow.Persistence;

/// <summary>
/// Read-only surface for assistant-produced artifact events. API endpoints (download
/// streaming, conversation-load hydration, save-from-rail) inject this interface so they
/// can list and look up events without inadvertently gaining write access.
/// <para/>
/// sc-799 (AA-8): the writer surface lives on <see cref="IAssistantArtifactRepository"/>
/// — splitting the two so a casual reader can't bypass the recorder by injecting the
/// repository directly. Bytes stay on disk in the per-conversation workspace; this
/// repository owns only the metadata rows. See <c>docs/assistant-artifacts.md</c> for the
/// full design.
/// </summary>
public interface IAssistantArtifactReadRepository
{
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
}

/// <summary>
/// Full read+write surface — the canonical write path is wrapped by
/// <c>CodeFlow.Api.Assistant.Artifacts.IArtifactRecorder</c>, which combines the writes with
/// supersession + supersession-clear bookkeeping in one call.
/// <para/>
/// sc-799 (AA-8): direct callers of the write methods are NOT supported outside the
/// recorder. Producer tools, endpoints, and tests must depend on
/// <c>IArtifactRecorder</c>; the recorder's implementation is the only sanctioned writer.
/// New producers added in Phase 3 (<c>diagnose_trace</c>, evidence bundles) follow the
/// same contract — see <c>docs/assistant-artifacts.md</c> §"Implementing a new artifact
/// producer".
/// </summary>
public interface IAssistantArtifactRepository : IAssistantArtifactReadRepository
{
    /// <summary>
    /// Append a new event. The repository assigns the next monotonic
    /// <see cref="AssistantArtifactEvent.Sequence"/> per conversation and bumps the
    /// conversation's <c>UpdatedAtUtc</c>. Internal write — call via
    /// <c>IArtifactRecorder.RecordAsync</c>.
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
    /// Marks every active event with the given <paramref name="name"/> superseded by the given
    /// <paramref name="supersedingEventId"/>. "Active" = not already superseded and not expired.
    /// Idempotent: a second call with the same arguments is a no-op. Used for draft replacement
    /// (set / patch / clear) where the new event takes the spot of the prior draft for that name.
    /// Internal write — call via <c>IArtifactRecorder.RecordAsync(supersedesPriorByName: true)</c>
    /// or <c>IArtifactRecorder.ClearByNameAsync</c>.
    /// </summary>
    Task<int> MarkActiveSupersededByNameAsync(
        Guid conversationId,
        string name,
        Guid supersedingEventId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks the event keyed by snapshot id as expired (file consumed). The row stays for
    /// audit/listing; the download endpoint surfaces it as gone. Returns the number of rows
    /// updated (0 if no event references the snapshot, otherwise 1). Internal write — call
    /// via <c>IArtifactRecorder.MarkSnapshotExpiredAsync</c>.
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
