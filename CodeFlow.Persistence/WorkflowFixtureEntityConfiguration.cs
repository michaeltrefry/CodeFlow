using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeFlow.Persistence;

public sealed class WorkflowFixtureEntityConfiguration : IEntityTypeConfiguration<WorkflowFixtureEntity>
{
    public void Configure(EntityTypeBuilder<WorkflowFixtureEntity> builder)
    {
        builder.ToTable("workflow_fixtures");

        builder.HasKey(fixture => fixture.Id);

        builder.Property(fixture => fixture.Id)
            .HasColumnName("id")
            .ValueGeneratedOnAdd();

        builder.Property(fixture => fixture.WorkflowKey)
            .HasColumnName("workflow_key")
            .HasMaxLength(192)
            .IsRequired();

        builder.Property(fixture => fixture.FixtureKey)
            .HasColumnName("fixture_key")
            .HasMaxLength(192)
            .IsRequired();

        builder.Property(fixture => fixture.DisplayName)
            .HasColumnName("display_name")
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(fixture => fixture.StartingInput)
            .HasColumnName("starting_input")
            .HasColumnType("longtext");

        builder.Property(fixture => fixture.MockResponsesJson)
            .HasColumnName("mock_responses_json")
            .HasColumnType("longtext")
            .IsRequired();

        builder.Property(fixture => fixture.CreatedAtUtc)
            .HasColumnName("created_at")
            .HasColumnType("datetime(6)")
            .IsRequired();

        builder.Property(fixture => fixture.UpdatedAtUtc)
            .HasColumnName("updated_at")
            .HasColumnType("datetime(6)")
            .IsRequired();

        builder.HasIndex(fixture => new { fixture.WorkflowKey, fixture.FixtureKey })
            .IsUnique();
    }
}
