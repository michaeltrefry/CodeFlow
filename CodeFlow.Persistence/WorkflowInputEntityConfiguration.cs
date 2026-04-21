using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeFlow.Persistence;

public sealed class WorkflowInputEntityConfiguration : IEntityTypeConfiguration<WorkflowInputEntity>
{
    public void Configure(EntityTypeBuilder<WorkflowInputEntity> builder)
    {
        builder.ToTable("workflow_inputs");

        builder.HasKey(input => input.Id);

        builder.Property(input => input.Id)
            .HasColumnName("id")
            .ValueGeneratedOnAdd();

        builder.Property(input => input.WorkflowId)
            .HasColumnName("workflow_id")
            .IsRequired();

        builder.Property(input => input.Key)
            .HasColumnName("key")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(input => input.DisplayName)
            .HasColumnName("display_name")
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(input => input.Kind)
            .HasColumnName("kind")
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(input => input.Required)
            .HasColumnName("required")
            .IsRequired();

        builder.Property(input => input.DefaultValueJson)
            .HasColumnName("default_value_json")
            .HasColumnType("longtext");

        builder.Property(input => input.Description)
            .HasColumnName("description")
            .HasColumnType("text");

        builder.Property(input => input.Ordinal)
            .HasColumnName("ordinal")
            .IsRequired();

        builder.HasOne(input => input.Workflow)
            .WithMany(workflow => workflow.Inputs)
            .HasForeignKey(input => input.WorkflowId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(input => new { input.WorkflowId, input.Key })
            .IsUnique();
    }
}
