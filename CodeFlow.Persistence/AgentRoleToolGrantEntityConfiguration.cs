using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeFlow.Persistence;

public sealed class AgentRoleToolGrantEntityConfiguration : IEntityTypeConfiguration<AgentRoleToolGrantEntity>
{
    public void Configure(EntityTypeBuilder<AgentRoleToolGrantEntity> builder)
    {
        builder.ToTable("agent_role_tool_grants");

        builder.HasKey(grant => grant.Id);

        builder.Property(grant => grant.Id)
            .HasColumnName("id")
            .ValueGeneratedOnAdd();

        builder.Property(grant => grant.RoleId)
            .HasColumnName("role_id")
            .IsRequired();

        builder.Property(grant => grant.Category)
            .HasColumnName("category")
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(grant => grant.ToolIdentifier)
            .HasColumnName("tool_identifier")
            .HasMaxLength(512)
            .IsRequired();

        builder.HasOne(grant => grant.Role)
            .WithMany()
            .HasForeignKey(grant => grant.RoleId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(grant => new { grant.RoleId, grant.Category, grant.ToolIdentifier }).IsUnique();
    }
}
