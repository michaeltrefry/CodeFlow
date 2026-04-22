using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeFlow.Persistence;

public sealed class WorkflowSagaLogicEvaluationEntityConfiguration : IEntityTypeConfiguration<WorkflowSagaLogicEvaluationEntity>
{
    public void Configure(EntityTypeBuilder<WorkflowSagaLogicEvaluationEntity> builder)
    {
        builder.ToTable("workflow_saga_logic_evaluations");

        builder.HasKey(e => new { e.SagaCorrelationId, e.Ordinal });

        builder.Property(e => e.SagaCorrelationId)
            .HasColumnName("saga_correlation_id")
            .IsRequired();

        builder.Property(e => e.Ordinal)
            .HasColumnName("ordinal")
            .IsRequired();

        builder.Property(e => e.TraceId)
            .HasColumnName("trace_id")
            .IsRequired();

        builder.Property(e => e.NodeId)
            .HasColumnName("node_id")
            .IsRequired();

        builder.Property(e => e.OutputPortName)
            .HasColumnName("output_port_name")
            .HasMaxLength(64);

        builder.Property(e => e.RoundId)
            .HasColumnName("round_id")
            .IsRequired();

        builder.Property(e => e.DurationTicks)
            .HasColumnName("duration_ticks")
            .IsRequired();

        builder.Property(e => e.LogsJson)
            .HasColumnName("logs_json")
            .HasColumnType("longtext")
            .IsRequired();

        builder.Property(e => e.FailureKind)
            .HasColumnName("failure_kind")
            .HasMaxLength(64);

        builder.Property(e => e.FailureMessage)
            .HasColumnName("failure_message")
            .HasMaxLength(1024);

        builder.Property(e => e.RecordedAtUtc)
            .HasColumnName("recorded_at")
            .HasColumnType("datetime(6)")
            .IsRequired();

        builder.HasIndex(e => new { e.TraceId, e.Ordinal });
    }
}
