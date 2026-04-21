using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeFlow.Persistence;

public sealed class SkillEntityConfiguration : IEntityTypeConfiguration<SkillEntity>
{
    public void Configure(EntityTypeBuilder<SkillEntity> builder)
    {
        builder.ToTable("skills");

        builder.HasKey(skill => skill.Id);

        builder.Property(skill => skill.Id)
            .HasColumnName("id")
            .ValueGeneratedOnAdd();

        builder.Property(skill => skill.Name)
            .HasColumnName("name")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(skill => skill.Body)
            .HasColumnName("body")
            .HasColumnType("longtext")
            .IsRequired();

        builder.Property(skill => skill.CreatedAtUtc)
            .HasColumnName("created_at")
            .HasColumnType("datetime(6)")
            .IsRequired();

        builder.Property(skill => skill.CreatedBy)
            .HasColumnName("created_by")
            .HasMaxLength(128);

        builder.Property(skill => skill.UpdatedAtUtc)
            .HasColumnName("updated_at")
            .HasColumnType("datetime(6)")
            .IsRequired();

        builder.Property(skill => skill.UpdatedBy)
            .HasColumnName("updated_by")
            .HasMaxLength(128);

        builder.Property(skill => skill.IsArchived)
            .HasColumnName("is_archived")
            .HasDefaultValue(false)
            .IsRequired();

        builder.HasIndex(skill => skill.Name).IsUnique();
        builder.HasIndex(skill => skill.IsArchived);
    }
}
