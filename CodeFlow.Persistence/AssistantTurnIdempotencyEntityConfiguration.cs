using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeFlow.Persistence;

public sealed class AssistantTurnIdempotencyEntityConfiguration : IEntityTypeConfiguration<AssistantTurnIdempotencyEntity>
{
    public void Configure(EntityTypeBuilder<AssistantTurnIdempotencyEntity> builder)
    {
        builder.ToTable("assistant_turn_idempotency");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id)
            .HasColumnName("id")
            .IsRequired();

        builder.Property(e => e.ConversationId)
            .HasColumnName("conversation_id")
            .IsRequired();

        builder.Property(e => e.IdempotencyKey)
            .HasColumnName("idempotency_key")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(e => e.UserId)
            .HasColumnName("user_id")
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(e => e.RequestHash)
            .HasColumnName("request_hash")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(e => e.Status)
            .HasColumnName("status")
            .HasConversion<int>()
            .IsRequired();

        builder.Property(e => e.EventsJson)
            .HasColumnName("events_json")
            .HasColumnType("longtext")
            .IsRequired();

        builder.Property(e => e.CreatedAtUtc)
            .HasColumnName("created_at")
            .HasColumnType("datetime(6)")
            .IsRequired();

        builder.Property(e => e.CompletedAtUtc)
            .HasColumnName("completed_at")
            .HasColumnType("datetime(6)");

        builder.Property(e => e.ExpiresAtUtc)
            .HasColumnName("expires_at")
            .HasColumnType("datetime(6)")
            .IsRequired();

        builder.HasIndex(e => new { e.ConversationId, e.IdempotencyKey }).IsUnique();
        builder.HasIndex(e => e.ExpiresAtUtc);
    }
}
