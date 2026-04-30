using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeFlow.Persistence.Notifications;

public sealed class NotificationProviderEntityConfiguration
    : IEntityTypeConfiguration<NotificationProviderEntity>
{
    public void Configure(EntityTypeBuilder<NotificationProviderEntity> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("notification_providers");

        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.Id)
            .HasColumnName("id")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(entity => entity.DisplayName)
            .HasColumnName("display_name")
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(entity => entity.Channel)
            .HasColumnName("channel")
            .IsRequired();

        builder.Property(entity => entity.EndpointUrl)
            .HasColumnName("endpoint_url")
            .HasMaxLength(512);

        builder.Property(entity => entity.FromAddress)
            .HasColumnName("from_address")
            .HasMaxLength(256);

        builder.Property(entity => entity.EncryptedCredential)
            .HasColumnName("encrypted_credential")
            .HasColumnType("varbinary(2048)");

        builder.Property(entity => entity.AdditionalConfigJson)
            .HasColumnName("additional_config_json")
            .HasColumnType("longtext");

        builder.Property(entity => entity.Enabled)
            .HasColumnName("enabled")
            .IsRequired();

        builder.Property(entity => entity.IsArchived)
            .HasColumnName("is_archived")
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

        builder.HasIndex(entity => entity.Channel);
    }
}
