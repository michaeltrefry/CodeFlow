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

        builder.Property(node => node.OutputScript)
            .HasColumnName("output_script")
            .HasColumnType("longtext");

        builder.Property(node => node.InputScript)
            .HasColumnName("input_script")
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

        builder.Property(node => node.SubflowKey)
            .HasColumnName("subflow_key")
            .HasMaxLength(128);

        builder.Property(node => node.SubflowVersion)
            .HasColumnName("subflow_version");

        builder.Property(node => node.ReviewMaxRounds)
            .HasColumnName("review_max_rounds");

        builder.Property(node => node.LoopDecision)
            .HasColumnName("loop_decision")
            .HasMaxLength(64);

        builder.Property(node => node.OptOutLastRoundReminder)
            .HasColumnName("opt_out_last_round_reminder")
            .HasDefaultValue(false)
            .IsRequired();

        builder.Property(node => node.RejectionHistoryConfigJson)
            .HasColumnName("rejection_history_config_json")
            .HasColumnType("longtext");

        builder.Property(node => node.MirrorOutputToWorkflowVar)
            .HasColumnName("mirror_output_to_workflow_var")
            .HasMaxLength(128);

        builder.Property(node => node.OutputPortReplacementsJson)
            .HasColumnName("output_port_replacements_json")
            .HasColumnType("longtext");

        builder.Property(node => node.Template)
            .HasColumnName("template")
            .HasColumnType("longtext");

        builder.Property(node => node.OutputType)
            .HasColumnName("output_type")
            .HasMaxLength(32)
            .HasDefaultValue("string")
            .IsRequired();

        builder.HasOne(node => node.Workflow)
            .WithMany(workflow => workflow.Nodes)
            .HasForeignKey(node => node.WorkflowId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(node => new { node.WorkflowId, node.NodeId })
            .IsUnique();
    }
}
