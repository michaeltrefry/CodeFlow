using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeFlow.Persistence;

public sealed class WorkflowSagaStateEntityConfiguration : IEntityTypeConfiguration<WorkflowSagaStateEntity>
{
    public void Configure(EntityTypeBuilder<WorkflowSagaStateEntity> builder)
    {
        builder.ToTable("workflow_sagas");

        builder.HasKey(saga => saga.CorrelationId);

        builder.Property(saga => saga.CorrelationId)
            .HasColumnName("correlation_id")
            .IsRequired();

        builder.Property(saga => saga.TraceId)
            .HasColumnName("trace_id")
            .IsRequired();

        builder.Property(saga => saga.CurrentState)
            .HasColumnName("current_state")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(saga => saga.CurrentNodeId)
            .HasColumnName("current_node_id")
            .IsRequired();

        builder.Property(saga => saga.CurrentAgentKey)
            .HasColumnName("current_agent_key")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(saga => saga.CurrentRoundId)
            .HasColumnName("current_round_id")
            .IsRequired();

        builder.Property(saga => saga.RoundCount)
            .HasColumnName("round_count")
            .IsRequired();

        builder.Property(saga => saga.AgentVersionsJson)
            .HasColumnName("agent_versions_json")
            .HasColumnType("longtext")
            .IsRequired();

        builder.Property(saga => saga.DecisionHistoryJson)
            .HasColumnName("decision_history_json")
            .HasColumnType("longtext")
            .IsRequired();

        builder.Property(saga => saga.LogicEvaluationHistoryJson)
            .HasColumnName("logic_evaluation_history_json")
            .HasColumnType("longtext")
            .IsRequired();

        builder.Property(saga => saga.WorkflowKey)
            .HasColumnName("workflow_key")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(saga => saga.WorkflowVersion)
            .HasColumnName("workflow_version")
            .IsRequired();

        builder.Property(saga => saga.InputsJson)
            .HasColumnName("inputs_json")
            .HasColumnType("longtext")
            .IsRequired();

        builder.Property(saga => saga.CurrentInputRef)
            .HasColumnName("current_input_ref")
            .HasMaxLength(1024);

        builder.Property(saga => saga.CreatedAtUtc)
            .HasColumnName("created_at")
            .HasColumnType("datetime(6)")
            .IsRequired();

        builder.Property(saga => saga.UpdatedAtUtc)
            .HasColumnName("updated_at")
            .HasColumnType("datetime(6)")
            .IsRequired();

        builder.Property(saga => saga.Version)
            .HasColumnName("version")
            .IsRequired()
            .IsConcurrencyToken();

        builder.Property(saga => saga.EscalatedFromNodeId)
            .HasColumnName("escalated_from_node_id");

        builder.Property(saga => saga.FailureReason)
            .HasColumnName("failure_reason")
            .HasMaxLength(512);

        builder.Ignore(saga => saga.PendingTransition);

        builder.HasIndex(saga => new { saga.WorkflowKey, saga.WorkflowVersion });
    }
}
