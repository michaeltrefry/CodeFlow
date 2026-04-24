using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeFlow.Persistence;

public sealed class LlmProviderSettingsEntityConfiguration : IEntityTypeConfiguration<LlmProviderSettingsEntity>
{
    public void Configure(EntityTypeBuilder<LlmProviderSettingsEntity> builder)
    {
        builder.ToTable("llm_providers");

        builder.HasKey(s => s.Provider);

        builder.Property(s => s.Provider)
            .HasColumnName("provider")
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(s => s.EncryptedApiKey)
            .HasColumnName("encrypted_api_key")
            .HasColumnType("varbinary(1024)");

        builder.Property(s => s.EndpointUrl)
            .HasColumnName("endpoint_url")
            .HasMaxLength(512);

        builder.Property(s => s.ApiVersion)
            .HasColumnName("api_version")
            .HasMaxLength(32);

        builder.Property(s => s.ModelsJson)
            .HasColumnName("models_json")
            .HasColumnType("longtext");

        builder.Property(s => s.UpdatedBy)
            .HasColumnName("updated_by")
            .HasMaxLength(128);

        builder.Property(s => s.UpdatedAtUtc)
            .HasColumnName("updated_at")
            .HasColumnType("datetime(6)")
            .IsRequired();
    }
}
