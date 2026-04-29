using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeFlow.Persistence;

public sealed class AssistantSettingsEntityConfiguration : IEntityTypeConfiguration<AssistantSettingsEntity>
{
    public void Configure(EntityTypeBuilder<AssistantSettingsEntity> builder)
    {
        builder.ToTable("assistant_settings");

        builder.HasKey(e => e.Key);

        builder.Property(e => e.Key)
            .HasColumnName("key")
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(e => e.Provider)
            .HasColumnName("provider")
            .HasMaxLength(64);

        builder.Property(e => e.Model)
            .HasColumnName("model")
            .HasMaxLength(128);

        builder.Property(e => e.MaxTokensPerConversation)
            .HasColumnName("max_tokens_per_conversation");

        builder.Property(e => e.UpdatedBy)
            .HasColumnName("updated_by")
            .HasMaxLength(256);

        builder.Property(e => e.UpdatedAtUtc)
            .HasColumnName("updated_at")
            .HasColumnType("datetime(6)")
            .IsRequired();
    }
}
