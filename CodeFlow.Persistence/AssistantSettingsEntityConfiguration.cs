using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeFlow.Persistence;

public sealed class AssistantSettingsEntityConfiguration : IEntityTypeConfiguration<AssistantSettingsEntity>
{
    public void Configure(EntityTypeBuilder<AssistantSettingsEntity> builder)
    {
        builder.ToTable("assistant_settings");

        builder.HasKey(e => e.Key);

        builder.Property(e => e.Key)
            .HasColumnName("key")
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(e => e.Provider)
            .HasColumnName("provider")
            .HasMaxLength(64);

        builder.Property(e => e.Model)
            .HasColumnName("model")
            .HasMaxLength(128);

        builder.Property(e => e.MaxTokensPerConversation)
            .HasColumnName("max_tokens_per_conversation");

        builder.Property(e => e.AssignedAgentRoleId)
            .HasColumnName("assigned_agent_role_id");

        builder.Property(e => e.Instructions)
            .HasColumnName("instructions")
            .HasColumnType("longtext");

        builder.Property(e => e.UpdatedBy)
            .HasColumnName("updated_by")
            .HasMaxLength(256);

        builder.Property(e => e.UpdatedAtUtc)
            .HasColumnName("updated_at")
            .HasColumnType("datetime(6)")
            .IsRequired();

        // FK to agent_roles with ON DELETE SET NULL: deleting an assigned role detaches the
        // assistant from it rather than wiping the singleton settings row.
        builder.HasOne<AgentRoleEntity>()
            .WithMany()
            .HasForeignKey(e => e.AssignedAgentRoleId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
