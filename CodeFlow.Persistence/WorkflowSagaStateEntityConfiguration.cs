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

        builder.Property(saga => saga.CurrentRoundEnteredAtUtc)
            .HasColumnName("current_round_entered_at")
            .HasColumnType("datetime(6)")
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

        builder.Property(saga => saga.DecisionCount)
            .HasColumnName("decision_count")
            .HasDefaultValue(0)
            .IsRequired();

        builder.Property(saga => saga.LogicEvaluationCount)
            .HasColumnName("logic_evaluation_count")
            .HasDefaultValue(0)
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

        // Stored as TEXT (no length cap on the property): agent-supplied failure reasons can run
        // long (multi-sentence diagnostics, prompts, raw exception messages), and truncating them
        // at the storage layer used to fault the saga with a "Data too long" MySqlException —
        // killing the trace before the operator could read the cause. TEXT in MySQL holds up to
        // ~64 KB which is more than enough; no row-length constraint applies.
        builder.Property(saga => saga.FailureReason)
            .HasColumnName("failure_reason")
            .HasColumnType("text");

        builder.Property(saga => saga.ParentTraceId)
            .HasColumnName("parent_trace_id");

        builder.Property(saga => saga.ParentNodeId)
            .HasColumnName("parent_node_id");

        builder.Property(saga => saga.ParentRoundId)
            .HasColumnName("parent_round_id");

        builder.Property(saga => saga.SubflowDepth)
            .HasColumnName("subflow_depth")
            .HasDefaultValue(0)
            .IsRequired();

        builder.Property(saga => saga.WorkflowInputsJson)
            .HasColumnName("workflow_inputs_json")
            .HasColumnType("longtext");

        builder.Property(saga => saga.ParentReviewRound)
            .HasColumnName("parent_review_round");

        builder.Property(saga => saga.ParentReviewMaxRounds)
            .HasColumnName("parent_review_max_rounds");

        builder.Property(saga => saga.LastEffectivePort)
            .HasColumnName("last_effective_port")
            .HasMaxLength(128);

        builder.Property(saga => saga.ParentLoopDecision)
            .HasColumnName("parent_loop_decision")
            .HasMaxLength(64);

        builder.Property(saga => saga.CurrentSwarmNodeId)
            .HasColumnName("current_swarm_coordinator_node_id");

        builder.Property(saga => saga.PendingParallelRoundIdsJson)
            .HasColumnName("pending_parallel_round_ids_json")
            .HasColumnType("longtext");

        builder.Property(saga => saga.RepositoriesJson)
            .HasColumnName("repositories_json")
            .HasColumnType("text");

        builder.Property(saga => saga.TraceWorkDir)
            .HasColumnName("trace_work_dir")
            .HasColumnType("text");

        builder.HasIndex(saga => saga.ParentTraceId);

        builder.Ignore(saga => saga.PendingTransition);

        builder.HasIndex(saga => new { saga.WorkflowKey, saga.WorkflowVersion });

        builder.HasMany(saga => saga.Decisions)
            .WithOne()
            .HasForeignKey(d => d.SagaCorrelationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(saga => saga.LogicEvaluations)
            .WithOne()
            .HasForeignKey(e => e.SagaCorrelationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
