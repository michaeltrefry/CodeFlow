using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeFlow.Persistence;

public sealed class WorkflowSagaDecisionEntityConfiguration : IEntityTypeConfiguration<WorkflowSagaDecisionEntity>
{
    public void Configure(EntityTypeBuilder<WorkflowSagaDecisionEntity> builder)
    {
        builder.ToTable("workflow_saga_decisions");

        builder.HasKey(d => new { d.SagaCorrelationId, d.Ordinal });

        builder.Property(d => d.SagaCorrelationId)
            .HasColumnName("saga_correlation_id")
            .IsRequired();

        builder.Property(d => d.Ordinal)
            .HasColumnName("ordinal")
            .IsRequired();

        builder.Property(d => d.TraceId)
            .HasColumnName("trace_id")
            .IsRequired();

        builder.Property(d => d.AgentKey)
            .HasColumnName("agent_key")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(d => d.AgentVersion)
            .HasColumnName("agent_version")
            .IsRequired();

        builder.Property(d => d.Decision)
            .HasColumnName("decision")
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(d => d.DecisionPayloadJson)
            .HasColumnName("decision_payload_json")
            .HasColumnType("longtext");

        builder.Property(d => d.RoundId)
            .HasColumnName("round_id")
            .IsRequired();

        builder.Property(d => d.RecordedAtUtc)
            .HasColumnName("recorded_at")
            .HasColumnType("datetime(6)")
            .IsRequired();

        builder.Property(d => d.NodeId)
            .HasColumnName("node_id");

        builder.Property(d => d.OutputPortName)
            .HasColumnName("output_port_name")
            .HasMaxLength(64);

        builder.Property(d => d.InputRef)
            .HasColumnName("input_ref")
            .HasMaxLength(1024);

        builder.Property(d => d.OutputRef)
            .HasColumnName("output_ref")
            .HasMaxLength(1024);

        builder.HasIndex(d => new { d.TraceId, d.Ordinal });
    }
}
