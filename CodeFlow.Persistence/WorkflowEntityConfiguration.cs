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

        builder.Property(workflow => workflow.StartAgentKey)
            .HasColumnName("start_agent_key")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(workflow => workflow.EscalationAgentKey)
            .HasColumnName("escalation_agent_key")
            .HasMaxLength(128);

        builder.Property(workflow => workflow.MaxRoundsPerRound)
            .HasColumnName("max_rounds_per_round")
            .IsRequired();

        builder.Property(workflow => workflow.CreatedAtUtc)
            .HasColumnName("created_at")
            .HasColumnType("datetime(6)")
            .IsRequired();

        builder.HasIndex(workflow => new { workflow.Key, workflow.Version })
            .IsUnique();
    }
}
