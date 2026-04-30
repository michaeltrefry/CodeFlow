using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeFlow.Persistence.Notifications;

public sealed class NotificationTemplateEntityConfiguration
    : IEntityTypeConfiguration<NotificationTemplateEntity>
{
    public void Configure(EntityTypeBuilder<NotificationTemplateEntity> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("notification_templates");

        builder.HasKey(entity => new { entity.TemplateId, entity.Version });

        builder.Property(entity => entity.TemplateId)
            .HasColumnName("template_id")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(entity => entity.Version)
            .HasColumnName("version")
            .IsRequired();

        builder.Property(entity => entity.EventKind)
            .HasColumnName("event_kind")
            .IsRequired();

        builder.Property(entity => entity.Channel)
            .HasColumnName("channel")
            .IsRequired();

        builder.Property(entity => entity.SubjectTemplate)
            .HasColumnName("subject_template")
            .HasColumnType("longtext");

        builder.Property(entity => entity.BodyTemplate)
            .HasColumnName("body_template")
            .HasColumnType("longtext")
            .IsRequired();

        builder.Property(entity => entity.CreatedAtUtc)
            .HasColumnName("created_at")
            .HasColumnType("datetime(6)")
            .IsRequired();

        builder.Property(entity => entity.CreatedBy)
            .HasColumnName("created_by")
            .HasMaxLength(128);

        builder.Property(entity => entity.UpdatedAtUtc)
            .HasColumnName("updated_at")
            .HasColumnType("datetime(6)")
            .IsRequired();

        builder.Property(entity => entity.UpdatedBy)
            .HasColumnName("updated_by")
            .HasMaxLength(128);

        builder.HasIndex(entity => new { entity.EventKind, entity.Channel });
    }
}
