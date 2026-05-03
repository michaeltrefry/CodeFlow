using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeFlow.Persistence;

public sealed class WebSearchProviderSettingsEntityConfiguration
    : IEntityTypeConfiguration<WebSearchProviderSettingsEntity>
{
    public void Configure(EntityTypeBuilder<WebSearchProviderSettingsEntity> builder)
    {
        builder.ToTable("web_search_providers");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.Id)
            .HasColumnName("id")
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(s => s.Provider)
            .HasColumnName("provider")
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(s => s.EncryptedApiKey)
            .HasColumnName("encrypted_api_key")
            .HasColumnType("varbinary(1024)");

        builder.Property(s => s.EndpointUrl)
            .HasColumnName("endpoint_url")
            .HasMaxLength(512);

        builder.Property(s => s.UpdatedBy)
            .HasColumnName("updated_by")
            .HasMaxLength(128);

        builder.Property(s => s.UpdatedAtUtc)
            .HasColumnName("updated_at")
            .HasColumnType("datetime(6)")
            .IsRequired();
    }
}
