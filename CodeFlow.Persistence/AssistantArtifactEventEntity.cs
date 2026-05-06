namespace CodeFlow.Persistence;

/// <summary>
/// EF entity for the <c>assistant_artifact_events</c> table. See <see cref="AssistantArtifactEvent"/>
/// for the domain projection and field-by-field semantics. Bytes stay on disk in the per-conversation
/// workspace; this row is metadata only.
/// </summary>
public sealed class AssistantArtifactEventEntity
{
    public Guid Id { get; set; }

    public Guid ConversationId { get; set; }

    public Guid? MessageId { get; set; }

    /// <summary>1-based, monotonic per conversation. Unique with ConversationId.</summary>
    public int Sequence { get; set; }

    public ArtifactEventKind Kind { get; set; }

    public string Name { get; set; } = string.Empty;

    public string RelativePath { get; set; } = string.Empty;

    public Guid? SnapshotId { get; set; }

    public string? SummaryJson { get; set; }

    public Guid? SupersededByEventId { get; set; }

    public DateTime? ExpiredAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
