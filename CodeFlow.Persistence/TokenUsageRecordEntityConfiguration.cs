using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeFlow.Persistence;

public sealed class TokenUsageRecordEntityConfiguration : IEntityTypeConfiguration<TokenUsageRecordEntity>
{
    public void Configure(EntityTypeBuilder<TokenUsageRecordEntity> builder)
    {
        builder.ToTable("token_usage_records");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.Id)
            .HasColumnName("id")
            .IsRequired();

        builder.Property(r => r.TraceId)
            .HasColumnName("trace_id")
            .IsRequired();

        builder.Property(r => r.NodeId)
            .HasColumnName("node_id")
            .IsRequired();

        builder.Property(r => r.InvocationId)
            .HasColumnName("invocation_id")
            .IsRequired();

        builder.Property(r => r.ScopeChainJson)
            .HasColumnName("scope_chain_json")
            .HasColumnType("longtext")
            .IsRequired();

        builder.Property(r => r.Provider)
            .HasColumnName("provider")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(r => r.Model)
            .HasColumnName("model")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(r => r.RecordedAtUtc)
            .HasColumnName("recorded_at")
            .HasColumnType("datetime(6)")
            .IsRequired();

        builder.Property(r => r.UsageJson)
            .HasColumnName("usage_json")
            .HasColumnType("longtext")
            .IsRequired();

        builder.HasIndex(r => r.TraceId);
        builder.HasIndex(r => new { r.TraceId, r.NodeId });
        builder.HasIndex(r => r.InvocationId);
    }
}
