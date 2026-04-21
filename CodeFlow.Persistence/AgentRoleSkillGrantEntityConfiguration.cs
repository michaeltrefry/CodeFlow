using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeFlow.Persistence;

public sealed class AgentRoleSkillGrantEntityConfiguration : IEntityTypeConfiguration<AgentRoleSkillGrantEntity>
{
    public void Configure(EntityTypeBuilder<AgentRoleSkillGrantEntity> builder)
    {
        builder.ToTable("agent_role_skill_grants");

        builder.HasKey(grant => grant.Id);

        builder.Property(grant => grant.Id)
            .HasColumnName("id")
            .ValueGeneratedOnAdd();

        builder.Property(grant => grant.RoleId)
            .HasColumnName("role_id")
            .IsRequired();

        builder.Property(grant => grant.SkillId)
            .HasColumnName("skill_id")
            .IsRequired();

        builder.HasOne(grant => grant.Role)
            .WithMany()
            .HasForeignKey(grant => grant.RoleId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(grant => grant.Skill)
            .WithMany()
            .HasForeignKey(grant => grant.SkillId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(grant => new { grant.RoleId, grant.SkillId }).IsUnique();
        builder.HasIndex(grant => grant.SkillId);
    }
}
