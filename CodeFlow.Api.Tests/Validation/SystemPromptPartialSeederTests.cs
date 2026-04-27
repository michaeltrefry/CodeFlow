using CodeFlow.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace CodeFlow.Api.Tests.Validation;

/// <summary>
/// Unit tests for <see cref="SystemPromptPartialSeeder"/>: idempotency, body fidelity, and
/// non-interference with author-added partials at the same key.
/// </summary>
public sealed class SystemPromptPartialSeederTests
{
    [Fact]
    public async Task SeedAsync_OnFreshDatabase_InsertsEverySystemPartialAtV1()
    {
        await using var db = CreateInMemory();

        await SystemPromptPartialSeeder.SeedAsync(db);

        var rows = await db.PromptPartials.AsNoTracking().ToListAsync();
        rows.Should().HaveCount(SystemPromptPartials.All.Count);
        foreach (var expected in SystemPromptPartials.All)
        {
            var row = rows.Single(r => r.Key == expected.Key);
            row.Version.Should().Be(expected.Version);
            row.Body.Should().Be(expected.Body);
            row.IsSystemManaged.Should().BeTrue();
        }
    }

    [Fact]
    public async Task SeedAsync_RunTwice_IsIdempotent()
    {
        await using var db = CreateInMemory();

        await SystemPromptPartialSeeder.SeedAsync(db);
        await SystemPromptPartialSeeder.SeedAsync(db);

        var rows = await db.PromptPartials.AsNoTracking().ToListAsync();
        rows.Should().HaveCount(SystemPromptPartials.All.Count);
    }

    [Fact]
    public async Task SeedAsync_PreservesExistingAuthorPartialAtSameKey()
    {
        // Authors can add partials at any key, including @codeflow/* (rare but legal). Verify the
        // seeder doesn't clobber author content when keys collide.
        await using var db = CreateInMemory();
        db.PromptPartials.Add(new PromptPartialEntity
        {
            Key = SystemPromptPartials.ReviewerBaseKey,
            Version = 5,
            Body = "Author override at v5",
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = "human",
            IsSystemManaged = false,
        });
        await db.SaveChangesAsync();

        await SystemPromptPartialSeeder.SeedAsync(db);

        // The author's v5 entry persists; the seeder also added v1.
        var byVersion = await db.PromptPartials.AsNoTracking()
            .Where(p => p.Key == SystemPromptPartials.ReviewerBaseKey)
            .ToDictionaryAsync(p => p.Version);
        byVersion.Should().HaveCount(2);
        byVersion[5].Body.Should().Be("Author override at v5");
        byVersion[5].IsSystemManaged.Should().BeFalse();
        byVersion[1].IsSystemManaged.Should().BeTrue();
    }

    [Fact]
    public async Task SeedAsync_DoesNotMutateExistingSystemPartialBody()
    {
        // Imagine someone hand-edited a system partial in the DB. The seeder must not overwrite
        // — partials are immutable per (key, version). Operators upgrade by bumping the version
        // constant in code, not by re-running the seeder against a hand-edited row.
        await using var db = CreateInMemory();
        db.PromptPartials.Add(new PromptPartialEntity
        {
            Key = SystemPromptPartials.ReviewerBaseKey,
            Version = 1,
            Body = "Hand-edited body",
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = null,
            IsSystemManaged = true,
        });
        await db.SaveChangesAsync();

        await SystemPromptPartialSeeder.SeedAsync(db);

        var row = await db.PromptPartials.AsNoTracking()
            .SingleAsync(p => p.Key == SystemPromptPartials.ReviewerBaseKey && p.Version == 1);
        row.Body.Should().Be("Hand-edited body");
    }

    [Fact]
    public void All_ContainsThe5SpecifiedPartials()
    {
        // Smoke test the catalog matches the P1 spec.
        var keys = SystemPromptPartials.All.Select(p => p.Key).ToArray();
        keys.Should().BeEquivalentTo(new[]
        {
            SystemPromptPartials.ReviewerBaseKey,
            SystemPromptPartials.ProducerBaseKey,
            SystemPromptPartials.LastRoundReminderKey,
            SystemPromptPartials.NoMetadataSectionsKey,
            SystemPromptPartials.WriteBeforeSubmitKey,
        });
        SystemPromptPartials.All.Should().AllSatisfy(p =>
        {
            p.Version.Should().Be(1);
            p.Body.Should().NotBeNullOrWhiteSpace();
        });
    }

    private static CodeFlowDbContext CreateInMemory()
    {
        var options = new DbContextOptionsBuilder<CodeFlowDbContext>()
            .UseInMemoryDatabase($"seeder-tests-{Guid.NewGuid():N}")
            .Options;
        return new CodeFlowDbContext(options);
    }
}
