using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeFlow.Persistence.Authority;

public sealed class RefusalEventEntityConfiguration : IEntityTypeConfiguration<RefusalEventEntity>
{
    public void Configure(EntityTypeBuilder<RefusalEventEntity> builder)
    {
        builder.ToTable("refusal_events");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id)
            .HasColumnName("id")
            .IsRequired();

        builder.Property(e => e.TraceId)
            .HasColumnName("trace_id");

        builder.Property(e => e.AssistantConversationId)
            .HasColumnName("assistant_conversation_id");

        builder.Property(e => e.Stage)
            .HasColumnName("stage")
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(e => e.Code)
            .HasColumnName("code")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(e => e.Reason)
            .HasColumnName("reason")
            .HasColumnType("text")
            .IsRequired();

        builder.Property(e => e.Axis)
            .HasColumnName("axis")
            .HasMaxLength(64);

        builder.Property(e => e.Path)
            .HasColumnName("path")
            .HasMaxLength(512);

        builder.Property(e => e.DetailJson)
            .HasColumnName("detail_json")
            .HasColumnType("longtext");

        builder.Property(e => e.OccurredAtUtc)
            .HasColumnName("occurred_at")
            .HasColumnType("datetime(6)")
            .IsRequired();

        builder.HasIndex(e => new { e.TraceId, e.OccurredAtUtc });
        builder.HasIndex(e => new { e.AssistantConversationId, e.OccurredAtUtc });
        builder.HasIndex(e => new { e.Stage, e.OccurredAtUtc });
    }
}
