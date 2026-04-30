using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeFlow.Persistence.Replay;

public sealed class ReplayAttemptEntityConfiguration : IEntityTypeConfiguration<ReplayAttemptEntity>
{
    public void Configure(EntityTypeBuilder<ReplayAttemptEntity> builder)
    {
        builder.ToTable("replay_attempts");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id)
            .HasColumnName("id")
            .IsRequired();

        builder.Property(e => e.ParentTraceId)
            .HasColumnName("parent_trace_id")
            .IsRequired();

        builder.Property(e => e.LineageId)
            .HasColumnName("lineage_id")
            .IsRequired();

        builder.Property(e => e.ContentHash)
            .HasColumnName("content_hash")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(e => e.Generation)
            .HasColumnName("generation")
            .IsRequired();

        builder.Property(e => e.ReplayState)
            .HasColumnName("replay_state")
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(e => e.TerminalPort)
            .HasColumnName("terminal_port")
            .HasMaxLength(64);

        builder.Property(e => e.DriftLevel)
            .HasColumnName("drift_level")
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(e => e.Reason)
            .HasColumnName("reason")
            .HasMaxLength(256);

        builder.Property(e => e.CreatedAtUtc)
            .HasColumnName("created_at")
            .HasColumnType("datetime(6)")
            .IsRequired();

        builder.HasIndex(e => new { e.ParentTraceId, e.CreatedAtUtc });
        builder.HasIndex(e => e.LineageId);
    }
}
