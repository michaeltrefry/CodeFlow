using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeFlow.Persistence;

public sealed class HitlTaskEntityConfiguration : IEntityTypeConfiguration<HitlTaskEntity>
{
    public void Configure(EntityTypeBuilder<HitlTaskEntity> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("hitl_tasks");

        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.Id)
            .HasColumnName("id")
            .ValueGeneratedOnAdd();

        builder.Property(entity => entity.TraceId)
            .HasColumnName("trace_id")
            .IsRequired();

        builder.Property(entity => entity.RoundId)
            .HasColumnName("round_id")
            .IsRequired();

        builder.Property(entity => entity.NodeId)
            .HasColumnName("node_id")
            .IsRequired();

        builder.Property(entity => entity.AgentKey)
            .HasColumnName("agent_key")
            .IsRequired()
            .HasMaxLength(128);

        builder.Property(entity => entity.AgentVersion)
            .HasColumnName("agent_version")
            .IsRequired();

        builder.Property(entity => entity.WorkflowKey)
            .HasColumnName("workflow_key")
            .IsRequired()
            .HasMaxLength(128);

        builder.Property(entity => entity.WorkflowVersion)
            .HasColumnName("workflow_version")
            .IsRequired();

        builder.Property(entity => entity.InputRef)
            .HasColumnName("input_ref")
            .IsRequired()
            .HasMaxLength(1024);

        builder.Property(entity => entity.InputPreview)
            .HasColumnName("input_preview")
            .HasMaxLength(4096);

        builder.Property(entity => entity.State)
            .HasColumnName("state")
            .IsRequired();

        builder.Property(entity => entity.Decision)
            .HasColumnName("decision");

        builder.Property(entity => entity.DecisionPayloadJson)
            .HasColumnName("decision_payload_json")
            .HasColumnType("longtext");

        builder.Property(entity => entity.DeciderId)
            .HasColumnName("decider_id")
            .HasMaxLength(256);

        builder.Property(entity => entity.CreatedAtUtc)
            .HasColumnName("created_at")
            .HasColumnType("datetime(6)")
            .IsRequired();

        builder.Property(entity => entity.DecidedAtUtc)
            .HasColumnName("decided_at")
            .HasColumnType("datetime(6)");

        builder.HasIndex(entity => new { entity.TraceId, entity.RoundId, entity.AgentKey });

        builder.HasIndex(entity => entity.State);
    }
}
