using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeFlow.Persistence;

public sealed class AssistantConversationEntityConfiguration : IEntityTypeConfiguration<AssistantConversationEntity>
{
    public void Configure(EntityTypeBuilder<AssistantConversationEntity> builder)
    {
        builder.ToTable("assistant_conversations");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.Id)
            .HasColumnName("id")
            .IsRequired();

        builder.Property(c => c.UserId)
            .HasColumnName("user_id")
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(c => c.ScopeKind)
            .HasColumnName("scope_kind")
            .HasConversion<int>()
            .IsRequired();

        builder.Property(c => c.EntityType)
            .HasColumnName("entity_type")
            .HasMaxLength(64);

        builder.Property(c => c.EntityId)
            .HasColumnName("entity_id")
            .HasMaxLength(128);

        builder.Property(c => c.ScopeKey)
            .HasColumnName("scope_key")
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(c => c.SyntheticTraceId)
            .HasColumnName("synthetic_trace_id")
            .IsRequired();

        builder.Property(c => c.InputTokensTotal)
            .HasColumnName("input_tokens_total")
            .IsRequired()
            .HasDefaultValue(0L);

        builder.Property(c => c.OutputTokensTotal)
            .HasColumnName("output_tokens_total")
            .IsRequired()
            .HasDefaultValue(0L);

        builder.Property(c => c.ActiveWorkspaceSignature)
            .HasColumnName("active_workspace_signature")
            .HasMaxLength(128);

        builder.Property(c => c.CreatedAtUtc)
            .HasColumnName("created_at")
            .HasColumnType("datetime(6)")
            .IsRequired();

        builder.Property(c => c.UpdatedAtUtc)
            .HasColumnName("updated_at")
            .HasColumnType("datetime(6)")
            .IsRequired();

        builder.HasIndex(c => new { c.UserId, c.ScopeKey, c.UpdatedAtUtc });
    }
}

public sealed class AssistantMessageEntityConfiguration : IEntityTypeConfiguration<AssistantMessageEntity>
{
    public void Configure(EntityTypeBuilder<AssistantMessageEntity> builder)
    {
        builder.ToTable("assistant_messages");

        builder.HasKey(m => m.Id);

        builder.Property(m => m.Id)
            .HasColumnName("id")
            .IsRequired();

        builder.Property(m => m.ConversationId)
            .HasColumnName("conversation_id")
            .IsRequired();

        builder.Property(m => m.Sequence)
            .HasColumnName("sequence")
            .IsRequired();

        builder.Property(m => m.Role)
            .HasColumnName("role")
            .HasConversion<int>()
            .IsRequired();

        builder.Property(m => m.Content)
            .HasColumnName("content")
            .HasColumnType("longtext")
            .IsRequired();

        builder.Property(m => m.Provider)
            .HasColumnName("provider")
            .HasMaxLength(64);

        builder.Property(m => m.Model)
            .HasColumnName("model")
            .HasMaxLength(128);

        builder.Property(m => m.InvocationId)
            .HasColumnName("invocation_id");

        builder.Property(m => m.CreatedAtUtc)
            .HasColumnName("created_at")
            .HasColumnType("datetime(6)")
            .IsRequired();

        builder.HasIndex(m => new { m.ConversationId, m.Sequence }).IsUnique();
    }
}
