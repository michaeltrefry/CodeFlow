using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeFlow.Persistence.Authority;

public sealed class AgentInvocationAuthorityEntityConfiguration : IEntityTypeConfiguration<AgentInvocationAuthorityEntity>
{
    public void Configure(EntityTypeBuilder<AgentInvocationAuthorityEntity> builder)
    {
        builder.ToTable("agent_invocation_authority");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id)
            .HasColumnName("id")
            .IsRequired();

        builder.Property(e => e.TraceId)
            .HasColumnName("trace_id")
            .IsRequired();

        builder.Property(e => e.RoundId)
            .HasColumnName("round_id")
            .IsRequired();

        builder.Property(e => e.AgentKey)
            .HasColumnName("agent_key")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(e => e.AgentVersion)
            .HasColumnName("agent_version");

        builder.Property(e => e.WorkflowKey)
            .HasColumnName("workflow_key")
            .HasMaxLength(128);

        builder.Property(e => e.WorkflowVersion)
            .HasColumnName("workflow_version");

        builder.Property(e => e.EnvelopeJson)
            .HasColumnName("envelope_json")
            .HasColumnType("longtext")
            .IsRequired();

        builder.Property(e => e.BlockedAxesJson)
            .HasColumnName("blocked_axes_json")
            .HasColumnType("longtext")
            .IsRequired();

        builder.Property(e => e.TiersJson)
            .HasColumnName("tiers_json")
            .HasColumnType("longtext")
            .IsRequired();

        builder.Property(e => e.ResolvedAtUtc)
            .HasColumnName("resolved_at")
            .HasColumnType("datetime(6)")
            .IsRequired();

        builder.HasIndex(e => new { e.TraceId, e.ResolvedAtUtc });
        builder.HasIndex(e => new { e.AgentKey, e.ResolvedAtUtc });
    }
}
