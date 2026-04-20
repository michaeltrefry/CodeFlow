using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeFlow.Persistence;

public sealed class McpServerToolEntityConfiguration : IEntityTypeConfiguration<McpServerToolEntity>
{
    public void Configure(EntityTypeBuilder<McpServerToolEntity> builder)
    {
        builder.ToTable("mcp_server_tools");

        builder.HasKey(tool => tool.Id);

        builder.Property(tool => tool.Id)
            .HasColumnName("id")
            .ValueGeneratedOnAdd();

        builder.Property(tool => tool.ServerId)
            .HasColumnName("server_id")
            .IsRequired();

        builder.Property(tool => tool.ToolName)
            .HasColumnName("tool_name")
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(tool => tool.Description)
            .HasColumnName("description")
            .HasColumnType("text");

        builder.Property(tool => tool.ParametersJson)
            .HasColumnName("parameters_json")
            .HasColumnType("longtext");

        builder.Property(tool => tool.IsMutating)
            .HasColumnName("is_mutating")
            .IsRequired();

        builder.Property(tool => tool.SyncedAtUtc)
            .HasColumnName("synced_at")
            .HasColumnType("datetime(6)")
            .IsRequired();

        builder.HasOne(tool => tool.Server)
            .WithMany()
            .HasForeignKey(tool => tool.ServerId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(tool => new { tool.ServerId, tool.ToolName }).IsUnique();
    }
}
