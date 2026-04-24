using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeFlow.Persistence;

public sealed class WorkflowEntityConfiguration : IEntityTypeConfiguration<WorkflowEntity>
{
    public void Configure(EntityTypeBuilder<WorkflowEntity> builder)
    {
        builder.ToTable("workflows");

        builder.HasKey(workflow => workflow.Id);

        builder.Property(workflow => workflow.Id)
            .HasColumnName("id")
            .ValueGeneratedOnAdd();

        builder.Property(workflow => workflow.Key)
            .HasColumnName("key")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(workflow => workflow.Version)
            .HasColumnName("version")
            .IsRequired();

        builder.Property(workflow => workflow.Name)
            .HasColumnName("name")
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(workflow => workflow.MaxRoundsPerRound)
            .HasColumnName("max_rounds_per_round")
            .IsRequired();

        builder.Property(workflow => workflow.Category)
            .HasColumnName("category")
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(workflow => workflow.TagsJson)
            .HasColumnName("tags_json")
            .HasColumnType("longtext")
            .IsRequired();

        builder.Property(workflow => workflow.CreatedAtUtc)
            .HasColumnName("created_at")
            .HasColumnType("datetime(6)")
            .IsRequired();

        builder.HasIndex(workflow => new { workflow.Key, workflow.Version })
            .IsUnique();
    }
}
