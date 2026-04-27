using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeFlow.Persistence;

public sealed class PromptPartialEntityConfiguration : IEntityTypeConfiguration<PromptPartialEntity>
{
    public void Configure(EntityTypeBuilder<PromptPartialEntity> builder)
    {
        builder.ToTable("prompt_partials");

        builder.HasKey(partial => partial.Id);

        builder.Property(partial => partial.Id)
            .HasColumnName("id")
            .ValueGeneratedOnAdd();

        builder.Property(partial => partial.Key)
            .HasColumnName("key")
            .HasMaxLength(192)
            .IsRequired();

        builder.Property(partial => partial.Version)
            .HasColumnName("version")
            .IsRequired();

        builder.Property(partial => partial.Body)
            .HasColumnName("body")
            .HasColumnType("longtext")
            .IsRequired();

        builder.Property(partial => partial.CreatedAtUtc)
            .HasColumnName("created_at")
            .HasColumnType("datetime(6)")
            .IsRequired();

        builder.Property(partial => partial.CreatedBy)
            .HasColumnName("created_by")
            .HasMaxLength(128);

        builder.Property(partial => partial.IsSystemManaged)
            .HasColumnName("is_system_managed")
            .HasDefaultValue(false)
            .IsRequired();

        builder.HasIndex(partial => new { partial.Key, partial.Version })
            .IsUnique();
    }
}
