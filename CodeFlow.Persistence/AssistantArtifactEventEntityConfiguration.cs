using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeFlow.Persistence;

public sealed class AssistantArtifactEventEntityConfiguration : IEntityTypeConfiguration<AssistantArtifactEventEntity>
{
    public void Configure(EntityTypeBuilder<AssistantArtifactEventEntity> builder)
    {
        builder.ToTable("assistant_artifact_events");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id)
            .HasColumnName("id")
            .IsRequired();

        builder.Property(e => e.ConversationId)
            .HasColumnName("conversation_id")
            .IsRequired();

        builder.Property(e => e.MessageId)
            .HasColumnName("message_id");

        builder.Property(e => e.Sequence)
            .HasColumnName("sequence")
            .IsRequired();

        builder.Property(e => e.Kind)
            .HasColumnName("kind")
            .HasConversion<int>()
            .IsRequired();

        builder.Property(e => e.Name)
            .HasColumnName("name")
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(e => e.RelativePath)
            .HasColumnName("relative_path")
            .HasMaxLength(512)
            .IsRequired();

        builder.Property(e => e.SnapshotId)
            .HasColumnName("snapshot_id");

        builder.Property(e => e.SummaryJson)
            .HasColumnName("summary_json")
            .HasColumnType("longtext");

        builder.Property(e => e.SupersededByEventId)
            .HasColumnName("superseded_by_event_id");

        builder.Property(e => e.ExpiredAtUtc)
            .HasColumnName("expired_at")
            .HasColumnType("datetime(6)");

        builder.Property(e => e.CreatedAtUtc)
            .HasColumnName("created_at")
            .HasColumnType("datetime(6)")
            .IsRequired();

        // Per-conversation monotonic ordering. AssistantArtifactRepository.AddAsync assigns the
        // next sequence under a single SaveChanges so the unique index catches concurrent racers.
        builder.HasIndex(e => new { e.ConversationId, e.Sequence }).IsUnique();

        // Look up by snapshot id when the apply endpoint marks an event expired.
        builder.HasIndex(e => e.SnapshotId);

        // Conversation-scoped list query orders by sequence; the (conversation_id, sequence) index
        // already covers it. Keep an additional index on (conversation_id, expired_at) for the
        // "active artifacts" rail query in Phase 2; cheap and avoids a later migration.
        builder.HasIndex(e => new { e.ConversationId, e.ExpiredAtUtc });
    }
}
