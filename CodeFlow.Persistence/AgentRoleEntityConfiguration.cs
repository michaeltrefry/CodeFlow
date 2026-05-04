using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeFlow.Persistence;

public sealed class AgentRoleEntityConfiguration : IEntityTypeConfiguration<AgentRoleEntity>
{
    public void Configure(EntityTypeBuilder<AgentRoleEntity> builder)
    {
        builder.ToTable("agent_roles");

        builder.HasKey(role => role.Id);

        builder.Property(role => role.Id)
            .HasColumnName("id")
            .ValueGeneratedOnAdd();

        builder.Property(role => role.Key)
            .HasColumnName("key")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(role => role.DisplayName)
            .HasColumnName("display_name")
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(role => role.Description)
            .HasColumnName("description")
            .HasColumnType("text");

        builder.Property(role => role.TagsJson)
            .HasColumnName("tags_json")
            .HasColumnType("longtext")
            .HasDefaultValue("[]")
            .IsRequired();

        builder.Property(role => role.CreatedAtUtc)
            .HasColumnName("created_at")
            .HasColumnType("datetime(6)")
            .IsRequired();

        builder.Property(role => role.CreatedBy)
            .HasColumnName("created_by")
            .HasMaxLength(128);

        builder.Property(role => role.UpdatedAtUtc)
            .HasColumnName("updated_at")
            .HasColumnType("datetime(6)")
            .IsRequired();

        builder.Property(role => role.UpdatedBy)
            .HasColumnName("updated_by")
            .HasMaxLength(128);

        builder.Property(role => role.IsArchived)
            .HasColumnName("is_archived")
            .HasDefaultValue(false)
            .IsRequired();

        builder.Property(role => role.IsRetired)
            .HasColumnName("is_retired")
            .HasDefaultValue(false)
            .IsRequired();

        builder.Property(role => role.IsSystemManaged)
            .HasColumnName("is_system_managed")
            .HasDefaultValue(false)
            .IsRequired();

        builder.HasIndex(role => role.Key).IsUnique();
        builder.HasIndex(role => role.IsArchived);
        builder.HasIndex(role => role.IsRetired);
    }
}
