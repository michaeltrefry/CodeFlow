using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeFlow.Persistence;

public sealed class GitHostSettingsEntityConfiguration : IEntityTypeConfiguration<GitHostSettingsEntity>
{
    public void Configure(EntityTypeBuilder<GitHostSettingsEntity> builder)
    {
        builder.ToTable("git_host_settings");

        builder.HasKey(s => s.Key);

        builder.Property(s => s.Key)
            .HasColumnName("key")
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(s => s.Mode)
            .HasColumnName("mode")
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(s => s.BaseUrl)
            .HasColumnName("base_url")
            .HasMaxLength(512);

        builder.Property(s => s.EncryptedToken)
            .HasColumnName("encrypted_token")
            .HasColumnType("varbinary(1024)")
            .IsRequired();

        builder.Property(s => s.WorkingDirectoryMaxAgeDays)
            .HasColumnName("working_directory_max_age_days");

        builder.Property(s => s.LastVerifiedAtUtc)
            .HasColumnName("last_verified_at")
            .HasColumnType("datetime(6)");

        builder.Property(s => s.UpdatedBy)
            .HasColumnName("updated_by")
            .HasMaxLength(128);

        builder.Property(s => s.UpdatedAtUtc)
            .HasColumnName("updated_at")
            .HasColumnType("datetime(6)")
            .IsRequired();
    }
}
