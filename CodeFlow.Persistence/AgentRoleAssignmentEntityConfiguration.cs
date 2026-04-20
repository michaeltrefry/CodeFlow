using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeFlow.Persistence;

public sealed class AgentRoleAssignmentEntityConfiguration : IEntityTypeConfiguration<AgentRoleAssignmentEntity>
{
    public void Configure(EntityTypeBuilder<AgentRoleAssignmentEntity> builder)
    {
        builder.ToTable("agent_role_assignments");

        builder.HasKey(assignment => assignment.Id);

        builder.Property(assignment => assignment.Id)
            .HasColumnName("id")
            .ValueGeneratedOnAdd();

        builder.Property(assignment => assignment.AgentKey)
            .HasColumnName("agent_key")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(assignment => assignment.RoleId)
            .HasColumnName("role_id")
            .IsRequired();

        builder.Property(assignment => assignment.CreatedAtUtc)
            .HasColumnName("created_at")
            .HasColumnType("datetime(6)")
            .IsRequired();

        builder.HasOne(assignment => assignment.Role)
            .WithMany()
            .HasForeignKey(assignment => assignment.RoleId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(assignment => new { assignment.AgentKey, assignment.RoleId }).IsUnique();
        builder.HasIndex(assignment => assignment.AgentKey);
    }
}
