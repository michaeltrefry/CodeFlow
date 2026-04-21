using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeFlow.Persistence;

public sealed class WorkflowNodeEntityConfiguration : IEntityTypeConfiguration<WorkflowNodeEntity>
{
    public void Configure(EntityTypeBuilder<WorkflowNodeEntity> builder)
    {
        builder.ToTable("workflow_nodes");

        builder.HasKey(node => node.Id);

        builder.Property(node => node.Id)
            .HasColumnName("id")
            .ValueGeneratedOnAdd();

        builder.Property(node => node.WorkflowId)
            .HasColumnName("workflow_id")
            .IsRequired();

        builder.Property(node => node.NodeId)
            .HasColumnName("node_id")
            .IsRequired();

        builder.Property(node => node.Kind)
            .HasColumnName("kind")
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(node => node.AgentKey)
            .HasColumnName("agent_key")
            .HasMaxLength(128);

        builder.Property(node => node.AgentVersion)
            .HasColumnName("agent_version");

        builder.Property(node => node.Script)
            .HasColumnName("script")
            .HasColumnType("longtext");

        builder.Property(node => node.OutputPortsJson)
            .HasColumnName("output_ports_json")
            .HasColumnType("longtext")
            .IsRequired();

        builder.Property(node => node.LayoutX)
            .HasColumnName("layout_x")
            .IsRequired();

        builder.Property(node => node.LayoutY)
            .HasColumnName("layout_y")
            .IsRequired();

        builder.HasOne(node => node.Workflow)
            .WithMany(workflow => workflow.Nodes)
            .HasForeignKey(node => node.WorkflowId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(node => new { node.WorkflowId, node.NodeId })
            .IsUnique();
    }
}
