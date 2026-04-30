using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeFlow.Persistence.Notifications;

public sealed class NotificationDeliveryAttemptEntityConfiguration
    : IEntityTypeConfiguration<NotificationDeliveryAttemptEntity>
{
    public void Configure(EntityTypeBuilder<NotificationDeliveryAttemptEntity> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("notification_delivery_attempts");

        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.Id)
            .HasColumnName("id")
            .ValueGeneratedOnAdd();

        builder.Property(entity => entity.EventId)
            .HasColumnName("event_id")
            .IsRequired();

        builder.Property(entity => entity.EventKind)
            .HasColumnName("event_kind")
            .IsRequired();

        builder.Property(entity => entity.RouteId)
            .HasColumnName("route_id")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(entity => entity.ProviderId)
            .HasColumnName("provider_id")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(entity => entity.Status)
            .HasColumnName("status")
            .IsRequired();

        builder.Property(entity => entity.AttemptNumber)
            .HasColumnName("attempt_number")
            .IsRequired();

        builder.Property(entity => entity.AttemptedAtUtc)
            .HasColumnName("attempted_at")
            .HasColumnType("datetime(6)")
            .IsRequired();

        builder.Property(entity => entity.CompletedAtUtc)
            .HasColumnName("completed_at")
            .HasColumnType("datetime(6)");

        builder.Property(entity => entity.NormalizedDestination)
            .HasColumnName("normalized_destination")
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(entity => entity.ProviderMessageId)
            .HasColumnName("provider_message_id")
            .HasMaxLength(256);

        builder.Property(entity => entity.ErrorCode)
            .HasColumnName("error_code")
            .HasMaxLength(128);

        builder.Property(entity => entity.ErrorMessage)
            .HasColumnName("error_message")
            .HasMaxLength(1024);

        builder.Property(entity => entity.CreatedAtUtc)
            .HasColumnName("created_at")
            .HasColumnType("datetime(6)")
            .IsRequired();

        // Idempotency guard: the dispatcher must not record the same attempt twice for a given
        // (event, provider, destination, attempt_number) tuple — even under racing retries from
        // a redelivered MassTransit message. Pairs with a dedupe lookup the dispatcher performs
        // before invoking the provider.
        builder.HasIndex(
                entity => new { entity.EventId, entity.ProviderId, entity.NormalizedDestination, entity.AttemptNumber })
            .IsUnique();

        builder.HasIndex(entity => new { entity.EventId, entity.AttemptedAtUtc });
        builder.HasIndex(entity => new { entity.RouteId, entity.AttemptedAtUtc });
    }
}
