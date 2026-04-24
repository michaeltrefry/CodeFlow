using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeFlow.Persistence;

public sealed class AgentConfigEntityConfiguration : IEntityTypeConfiguration<AgentConfigEntity>
{
    public void Configure(EntityTypeBuilder<AgentConfigEntity> builder)
    {
        builder.ToTable("agents");

        builder.HasKey(agent => agent.Id);

        builder.Property(agent => agent.Id)
            .HasColumnName("id")
            .ValueGeneratedOnAdd();

        builder.Property(agent => agent.Key)
            .HasColumnName("key")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(agent => agent.Version)
            .HasColumnName("version")
            .IsRequired();

        builder.Property(agent => agent.ConfigJson)
            .HasColumnName("config_json")
            .HasColumnType("longtext")
            .IsRequired();

        builder.Property(agent => agent.CreatedAtUtc)
            .HasColumnName("created_at")
            .HasColumnType("datetime(6)")
            .IsRequired();

        builder.Property(agent => agent.CreatedBy)
            .HasColumnName("created_by")
            .HasMaxLength(128);

        builder.Property(agent => agent.IsActive)
            .HasColumnName("is_active")
            .IsRequired();

        builder.Property(agent => agent.IsRetired)
            .HasColumnName("is_retired")
            .HasDefaultValue(false)
            .IsRequired();

        builder.Property(agent => agent.OwningWorkflowKey)
            .HasColumnName("owning_workflow_key")
            .HasMaxLength(128);

        builder.Property(agent => agent.ForkedFromKey)
            .HasColumnName("forked_from_key")
            .HasMaxLength(128);

        builder.Property(agent => agent.ForkedFromVersion)
            .HasColumnName("forked_from_version");

        builder.HasIndex(agent => new { agent.Key, agent.Version })
            .IsUnique();

        builder.HasIndex(agent => new { agent.Key, agent.IsActive });

        builder.HasIndex(agent => new { agent.Key, agent.IsRetired });

        builder.HasIndex(agent => agent.OwningWorkflowKey);
    }
}
