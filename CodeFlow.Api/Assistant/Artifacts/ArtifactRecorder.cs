using CodeFlow.Persistence;

namespace CodeFlow.Api.Assistant.Artifacts;

public sealed class ArtifactRecorder(IAssistantArtifactRepository repository) : IArtifactRecorder
{
    public async Task<AssistantArtifactEvent> RecordAsync(
        Guid conversationId,
        ArtifactEventKind kind,
        string name,
        string relativePath,
        Guid? snapshotId,
        string? summaryJson,
        bool supersedesPriorByName,
        CancellationToken cancellationToken = default)
    {
        var added = await repository.AddAsync(
            conversationId,
            kind,
            name,
            relativePath,
            snapshotId,
            summaryJson,
            cancellationToken);

        if (supersedesPriorByName)
        {
            // Mark prior actives superseded AFTER the add so the new event has a real id to point
            // at. Self-supersession is excluded inside the repository call.
            await repository.MarkActiveSupersededByNameAsync(
                conversationId,
                name,
                added.Id,
                cancellationToken);
        }

        return added;
    }

    public async Task<int> ClearByNameAsync(
        Guid conversationId,
        string name,
        CancellationToken cancellationToken = default)
    {
        // Use Guid.Empty as the "cleared without replacement" marker. The UI treats a
        // SupersededByEventId of Guid.Empty as "removed" rather than "replaced by another pill"
        // so it renders a plain "draft cleared" lineage entry without trying to follow the link.
        return await repository.MarkActiveSupersededByNameAsync(
            conversationId,
            name,
            Guid.Empty,
            cancellationToken);
    }

    public Task<int> MarkSnapshotExpiredAsync(
        Guid snapshotId,
        CancellationToken cancellationToken = default) =>
        repository.MarkExpiredBySnapshotIdAsync(snapshotId, DateTime.UtcNow, cancellationToken);
}
