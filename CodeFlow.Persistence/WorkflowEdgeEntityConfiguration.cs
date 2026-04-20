using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeFlow.Persistence;

public sealed class WorkflowEdgeEntityConfiguration : IEntityTypeConfiguration<WorkflowEdgeEntity>
{
    public void Configure(EntityTypeBuilder<WorkflowEdgeEntity> builder)
    {
        builder.ToTable("workflow_edges");

        builder.HasKey(edge => edge.Id);

        builder.Property(edge => edge.Id)
            .HasColumnName("id")
            .ValueGeneratedOnAdd();

        builder.Property(edge => edge.WorkflowId)
            .HasColumnName("workflow_id")
            .IsRequired();

        builder.Property(edge => edge.FromAgentKey)
            .HasColumnName("from_agent_key")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(edge => edge.Decision)
            .HasColumnName("decision")
            .HasConversion<string>()
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(edge => edge.DiscriminatorJson)
            .HasColumnName("discriminator_json")
            .HasColumnType("longtext");

        builder.Property(edge => edge.ToAgentKey)
            .HasColumnName("to_agent_key")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(edge => edge.RotatesRound)
            .HasColumnName("rotates_round")
            .IsRequired();

        builder.Property(edge => edge.SortOrder)
            .HasColumnName("sort_order")
            .IsRequired();

        builder.HasOne(edge => edge.Workflow)
            .WithMany(workflow => workflow.Edges)
            .HasForeignKey(edge => edge.WorkflowId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(edge => new
        {
            edge.WorkflowId,
            edge.FromAgentKey,
            edge.Decision,
            edge.SortOrder
        });
    }
}
