using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeFlow.Persistence.Notifications;

public sealed class NotificationRouteEntityConfiguration
    : IEntityTypeConfiguration<NotificationRouteEntity>
{
    public void Configure(EntityTypeBuilder<NotificationRouteEntity> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("notification_routes");

        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.Id)
            .HasColumnName("id")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(entity => entity.EventKind)
            .HasColumnName("event_kind")
            .IsRequired();

        builder.Property(entity => entity.ProviderId)
            .HasColumnName("provider_id")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(entity => entity.TemplateId)
            .HasColumnName("template_id")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(entity => entity.TemplateVersion)
            .HasColumnName("template_version")
            .IsRequired();

        builder.Property(entity => entity.RecipientsJson)
            .HasColumnName("recipients_json")
            .HasColumnType("longtext")
            .IsRequired();

        builder.Property(entity => entity.MinimumSeverity)
            .HasColumnName("minimum_severity")
            .IsRequired();

        builder.Property(entity => entity.Enabled)
            .HasColumnName("enabled")
            .IsRequired();

        builder.Property(entity => entity.CreatedAtUtc)
            .HasColumnName("created_at")
            .HasColumnType("datetime(6)")
            .IsRequired();

        builder.Property(entity => entity.UpdatedAtUtc)
            .HasColumnName("updated_at")
            .HasColumnType("datetime(6)")
            .IsRequired();

        builder.HasIndex(entity => new { entity.EventKind, entity.Enabled });
        builder.HasIndex(entity => entity.ProviderId);
    }
}
