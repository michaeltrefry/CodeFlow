using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeFlow.Persistence;

public sealed class McpServerEntityConfiguration : IEntityTypeConfiguration<McpServerEntity>
{
    public void Configure(EntityTypeBuilder<McpServerEntity> builder)
    {
        builder.ToTable("mcp_servers");

        builder.HasKey(server => server.Id);

        builder.Property(server => server.Id)
            .HasColumnName("id")
            .ValueGeneratedOnAdd();

        builder.Property(server => server.Key)
            .HasColumnName("key")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(server => server.DisplayName)
            .HasColumnName("display_name")
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(server => server.Transport)
            .HasColumnName("transport")
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(server => server.EndpointUrl)
            .HasColumnName("endpoint_url")
            .HasMaxLength(1024)
            .IsRequired();

        builder.Property(server => server.BearerTokenCipher)
            .HasColumnName("bearer_token_cipher")
            .HasColumnType("varbinary(1024)");

        builder.Property(server => server.HealthStatus)
            .HasColumnName("health_status")
            .HasConversion<string>()
            .HasMaxLength(32)
            .HasDefaultValue(McpServerHealthStatus.Unverified)
            .IsRequired();

        builder.Property(server => server.LastVerifiedAtUtc)
            .HasColumnName("last_verified_at")
            .HasColumnType("datetime(6)");

        builder.Property(server => server.LastVerificationError)
            .HasColumnName("last_verification_error")
            .HasColumnType("text");

        builder.Property(server => server.CreatedAtUtc)
            .HasColumnName("created_at")
            .HasColumnType("datetime(6)")
            .IsRequired();

        builder.Property(server => server.CreatedBy)
            .HasColumnName("created_by")
            .HasMaxLength(128);

        builder.Property(server => server.UpdatedAtUtc)
            .HasColumnName("updated_at")
            .HasColumnType("datetime(6)")
            .IsRequired();

        builder.Property(server => server.UpdatedBy)
            .HasColumnName("updated_by")
            .HasMaxLength(128);

        builder.Property(server => server.IsArchived)
            .HasColumnName("is_archived")
            .HasDefaultValue(false)
            .IsRequired();

        builder.HasIndex(server => server.Key).IsUnique();
        builder.HasIndex(server => server.IsArchived);
    }
}
