using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MariaDb;

namespace CodeFlow.Persistence.Tests;

public sealed class SkillRepositoryTests : IAsyncLifetime
{
    private readonly MariaDbContainer mariaDbContainer = new MariaDbBuilder("mariadb:11.4")
        .WithDatabase("codeflow_tests")
        .WithUsername("codeflow")
        .WithPassword("codeflow_dev")
        .Build();

    private string? connectionString;

    public async Task InitializeAsync()
    {
        await mariaDbContainer.StartAsync();
        connectionString = mariaDbContainer.GetConnectionString();

        await using var dbContext = CreateDbContext();
        await dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await mariaDbContainer.DisposeAsync();
    }

    [Fact]
    public async Task CreateAsync_round_trips_skill_metadata()
    {
        var name = $"socratic-{Guid.NewGuid():N}";

        await using var context = CreateDbContext();
        var repo = new SkillRepository(context);

        var id = await repo.CreateAsync(new SkillCreate(
            Name: name,
            Body: "# Opening\nAsk one question.",
            CreatedBy: "tester"));

        var skill = await repo.GetAsync(id);
        skill.Should().NotBeNull();
        skill!.Name.Should().Be(name);
        skill.Body.Should().Be("# Opening\nAsk one question.");
        skill.CreatedBy.Should().Be("tester");
        skill.IsArchived.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateAsync_changes_name_and_body_and_updated_metadata()
    {
        var name = $"skill-{Guid.NewGuid():N}";

        await using var context = CreateDbContext();
        var repo = new SkillRepository(context);

        var id = await repo.CreateAsync(new SkillCreate(name, "original body", "creator"));

        var newName = $"renamed-{Guid.NewGuid():N}";
        await repo.UpdateAsync(id, new SkillUpdate(newName, "new body", "editor"));

        var skill = await repo.GetAsync(id);
        skill!.Name.Should().Be(newName);
        skill.Body.Should().Be("new body");
        skill.UpdatedBy.Should().Be("editor");
    }

    [Fact]
    public async Task ArchiveAsync_marks_skill_archived_and_hides_from_default_list()
    {
        var name = $"skill-{Guid.NewGuid():N}";

        await using var context = CreateDbContext();
        var repo = new SkillRepository(context);

        var id = await repo.CreateAsync(new SkillCreate(name, "body", null));
        await repo.ArchiveAsync(id);

        (await repo.ListAsync(includeArchived: false)).Should().NotContain(s => s.Id == id);
        (await repo.ListAsync(includeArchived: true)).Should().Contain(s => s.Id == id);
    }

    [Fact]
    public async Task UpdateAsync_throws_SkillNotFoundException_for_unknown_id()
    {
        await using var context = CreateDbContext();
        var repo = new SkillRepository(context);

        var act = () => repo.UpdateAsync(99999, new SkillUpdate("x", "y", null));

        await act.Should().ThrowAsync<SkillNotFoundException>();
    }

    [Fact]
    public async Task GetByNameAsync_returns_skill_when_name_matches()
    {
        var name = $"skill-{Guid.NewGuid():N}";

        await using var context = CreateDbContext();
        var repo = new SkillRepository(context);

        var id = await repo.CreateAsync(new SkillCreate(name, "body", null));

        var found = await repo.GetByNameAsync(name);
        found.Should().NotBeNull();
        found!.Id.Should().Be(id);
    }

    [Fact]
    public async Task CreateAsync_throws_when_name_is_duplicate()
    {
        var name = $"skill-{Guid.NewGuid():N}";

        await using var context = CreateDbContext();
        var repo = new SkillRepository(context);

        await repo.CreateAsync(new SkillCreate(name, "one", null));

        var act = () => repo.CreateAsync(new SkillCreate(name, "two", null));
        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task ReplaceSkillGrantsAsync_replaces_full_skill_set_on_role()
    {
        await using var context = CreateDbContext();
        var roles = new AgentRoleRepository(context);
        var skills = new SkillRepository(context);

        var roleId = await roles.CreateAsync(new AgentRoleCreate($"r-{Guid.NewGuid():N}", "R", null, null));
        var s1 = await skills.CreateAsync(new SkillCreate($"s1-{Guid.NewGuid():N}", "a", null));
        var s2 = await skills.CreateAsync(new SkillCreate($"s2-{Guid.NewGuid():N}", "b", null));
        var s3 = await skills.CreateAsync(new SkillCreate($"s3-{Guid.NewGuid():N}", "c", null));

        await roles.ReplaceSkillGrantsAsync(roleId, new[] { s1, s2 });
        (await roles.GetSkillGrantsAsync(roleId)).Should().BeEquivalentTo(new[] { s1, s2 });

        await roles.ReplaceSkillGrantsAsync(roleId, new[] { s2, s3 });
        (await roles.GetSkillGrantsAsync(roleId)).Should().BeEquivalentTo(new[] { s2, s3 });
    }

    [Fact]
    public async Task ReplaceSkillGrantsAsync_dedupes_identical_ids()
    {
        await using var context = CreateDbContext();
        var roles = new AgentRoleRepository(context);
        var skills = new SkillRepository(context);

        var roleId = await roles.CreateAsync(new AgentRoleCreate($"r-{Guid.NewGuid():N}", "R", null, null));
        var s1 = await skills.CreateAsync(new SkillCreate($"s-{Guid.NewGuid():N}", "a", null));

        await roles.ReplaceSkillGrantsAsync(roleId, new[] { s1, s1, s1 });
        (await roles.GetSkillGrantsAsync(roleId)).Should().ContainSingle().Which.Should().Be(s1);
    }

    [Fact]
    public async Task ReplaceSkillGrantsAsync_rejects_unknown_skill_ids()
    {
        await using var context = CreateDbContext();
        var roles = new AgentRoleRepository(context);

        var roleId = await roles.CreateAsync(new AgentRoleCreate($"r-{Guid.NewGuid():N}", "R", null, null));

        var act = () => roles.ReplaceSkillGrantsAsync(roleId, new[] { 999999L });
        await act.Should().ThrowAsync<SkillNotFoundException>();
    }

    [Fact]
    public async Task Deleting_a_skill_cascades_to_role_grants()
    {
        await using var context = CreateDbContext();
        var roles = new AgentRoleRepository(context);
        var skills = new SkillRepository(context);

        var roleId = await roles.CreateAsync(new AgentRoleCreate($"r-{Guid.NewGuid():N}", "R", null, null));
        var skillId = await skills.CreateAsync(new SkillCreate($"s-{Guid.NewGuid():N}", "body", null));

        await roles.ReplaceSkillGrantsAsync(roleId, new[] { skillId });

        var skillEntity = await context.Skills.SingleAsync(s => s.Id == skillId);
        context.Skills.Remove(skillEntity);
        await context.SaveChangesAsync();

        (await roles.GetSkillGrantsAsync(roleId)).Should().BeEmpty();
    }

    private CodeFlowDbContext CreateDbContext()
    {
        var builder = new DbContextOptionsBuilder<CodeFlowDbContext>();
        CodeFlowDbContextOptions.Configure(builder, connectionString!);
        return new CodeFlowDbContext(builder.Options);
    }
}
