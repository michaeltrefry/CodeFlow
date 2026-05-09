using FluentAssertions;
using Microsoft.EntityFrameworkCore;
namespace CodeFlow.Persistence.Tests;

[Collection(PersistenceMariaDbCollection.Name)]
public sealed class AgentRoleRepositoryTests : IAsyncLifetime
{
    private readonly SharedMariaDbFixture mariaDb;
    private const string DatabaseName = "test_agentrolerepositorytests";
    private string? connectionString;



    public AgentRoleRepositoryTests(SharedMariaDbFixture mariaDb)

    {

        this.mariaDb = mariaDb;

    }


    public async Task InitializeAsync()
    {
        connectionString = await mariaDb.EnsureDatabaseAsync(DatabaseName);

        await using var dbContext = CreateDbContext();
        await dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await mariaDb.DropDatabaseAsync(DatabaseName);
    }

    [Fact]
    public async Task CreateAsync_round_trips_role_metadata()
    {
        var key = $"reader-{Guid.NewGuid():N}";

        await using var context = CreateDbContext();
        var repo = new AgentRoleRepository(context);

        var id = await repo.CreateAsync(new AgentRoleCreate(
            Key: key,
            DisplayName: "Reader",
            Description: "Read-only role",
            CreatedBy: "tester"));

        var role = await repo.GetAsync(id);
        role.Should().NotBeNull();
        role!.Key.Should().Be(key);
        role.DisplayName.Should().Be("Reader");
        role.Description.Should().Be("Read-only role");
        role.CreatedBy.Should().Be("tester");
        role.IsArchived.Should().BeFalse();
        role.TagsOrEmpty.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAsync_normalizes_and_round_trips_tags()
    {
        var key = $"reader-{Guid.NewGuid():N}";

        await using var context = CreateDbContext();
        var repo = new AgentRoleRepository(context);

        var id = await repo.CreateAsync(new AgentRoleCreate(
            Key: key,
            DisplayName: "Reader",
            Description: null,
            CreatedBy: "tester",
            Tags:
            [
                " code-review ",
                "security",
                "CODE-REVIEW",
                "",
                "ops",
                "docs",
                "quality",
                "overflow"
            ]));

        var role = await repo.GetAsync(id);

        role.Should().NotBeNull();
        role!.TagsOrEmpty.Should().Equal("code-review", "security", "ops", "docs", "quality");
    }

    [Fact]
    public async Task UpdateAsync_replaces_tags_with_normalized_values()
    {
        var key = $"reader-{Guid.NewGuid():N}";

        await using var context = CreateDbContext();
        var repo = new AgentRoleRepository(context);

        var id = await repo.CreateAsync(new AgentRoleCreate(
            key,
            "Reader",
            null,
            "tester",
            ["old"]));

        await repo.UpdateAsync(id, new AgentRoleUpdate(
            DisplayName: "Reader 2",
            Description: "Updated",
            UpdatedBy: "tester",
            Tags: [" new ", "NEW", "ops"]));

        var role = await repo.GetAsync(id);

        role.Should().NotBeNull();
        role!.TagsOrEmpty.Should().Equal("new", "ops");
    }

    [Fact]
    public async Task UpdateAsync_preserves_existing_tags_when_tags_are_not_supplied()
    {
        var key = $"reader-{Guid.NewGuid():N}";

        await using var context = CreateDbContext();
        var repo = new AgentRoleRepository(context);

        var id = await repo.CreateAsync(new AgentRoleCreate(
            key,
            "Reader",
            null,
            "tester",
            ["stable"]));

        await repo.UpdateAsync(id, new AgentRoleUpdate(
            DisplayName: "Reader 2",
            Description: "Updated",
            UpdatedBy: "tester"));

        var role = await repo.GetAsync(id);

        role.Should().NotBeNull();
        role!.TagsOrEmpty.Should().Equal("stable");
    }

    [Fact]
    public async Task ReplaceGrantsAsync_replaces_the_full_grant_set()
    {
        var key = $"role-{Guid.NewGuid():N}";

        await using var context = CreateDbContext();
        var repo = new AgentRoleRepository(context);

        var id = await repo.CreateAsync(new AgentRoleCreate(key, "Role", null, null));

        await repo.ReplaceGrantsAsync(id,
        [
            new AgentRoleToolGrant(AgentRoleToolCategory.Host, "echo"),
            new AgentRoleToolGrant(AgentRoleToolCategory.Host, "now"),
            new AgentRoleToolGrant(AgentRoleToolCategory.Mcp, "mcp:artifacts:read"),
        ]);

        var firstPass = await repo.GetGrantsAsync(id);
        firstPass.Should().HaveCount(3);

        await repo.ReplaceGrantsAsync(id,
        [
            new AgentRoleToolGrant(AgentRoleToolCategory.Mcp, "mcp:search:query"),
        ]);

        var secondPass = await repo.GetGrantsAsync(id);
        secondPass.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new AgentRoleToolGrant(AgentRoleToolCategory.Mcp, "mcp:search:query"));
    }

    [Fact]
    public async Task ReplaceGrantsAsync_dedupes_identical_grants()
    {
        var key = $"role-{Guid.NewGuid():N}";

        await using var context = CreateDbContext();
        var repo = new AgentRoleRepository(context);

        var id = await repo.CreateAsync(new AgentRoleCreate(key, "Role", null, null));

        await repo.ReplaceGrantsAsync(id,
        [
            new AgentRoleToolGrant(AgentRoleToolCategory.Host, "echo"),
            new AgentRoleToolGrant(AgentRoleToolCategory.Host, "echo"),
            new AgentRoleToolGrant(AgentRoleToolCategory.Mcp, "mcp:artifacts:read"),
        ]);

        var grants = await repo.GetGrantsAsync(id);
        grants.Should().HaveCount(2);
    }

    [Fact]
    public async Task ReplaceAssignmentsAsync_replaces_agent_role_membership()
    {
        var agentKey = $"agent-{Guid.NewGuid():N}";

        await using var context = CreateDbContext();
        var repo = new AgentRoleRepository(context);

        var roleA = await repo.CreateAsync(new AgentRoleCreate($"role-a-{Guid.NewGuid():N}", "A", null, null));
        var roleB = await repo.CreateAsync(new AgentRoleCreate($"role-b-{Guid.NewGuid():N}", "B", null, null));
        var roleC = await repo.CreateAsync(new AgentRoleCreate($"role-c-{Guid.NewGuid():N}", "C", null, null));

        await repo.ReplaceAssignmentsForLatestAsync(agentKey, new[] { roleA, roleB });

        // Latest reads the highest agent_version row — agent_role_assignments has no real
        // agent rows here so the writer lands at agent_version=0 (the orphan placeholder).
        // Latest correctly returns those rows. AR-4 reshapes the writer for real agents.
        var afterFirst = await repo.GetRolesForAgentLatestAsync(agentKey);
        afterFirst.Select(r => r.Id).Should().BeEquivalentTo(new[] { roleA, roleB });

        await repo.ReplaceAssignmentsForLatestAsync(agentKey, new[] { roleB, roleC });

        var afterSecond = await repo.GetRolesForAgentLatestAsync(agentKey);
        afterSecond.Select(r => r.Id).Should().BeEquivalentTo(new[] { roleB, roleC });
    }

    [Fact]
    public async Task ReplaceAssignmentsAsync_rejects_unknown_role_ids()
    {
        var agentKey = $"agent-{Guid.NewGuid():N}";

        await using var context = CreateDbContext();
        var repo = new AgentRoleRepository(context);

        var realRole = await repo.CreateAsync(new AgentRoleCreate($"r-{Guid.NewGuid():N}", "R", null, null));

        var act = () => repo.ReplaceAssignmentsForLatestAsync(agentKey, new[] { realRole, 999999L });

        await act.Should().ThrowAsync<AgentRoleNotFoundException>();
    }

    [Fact]
    public async Task Archiving_a_role_cascades_to_grants_and_assignments_when_row_is_removed()
    {
        var agentKey = $"agent-{Guid.NewGuid():N}";

        await using var context = CreateDbContext();
        var repo = new AgentRoleRepository(context);

        var roleId = await repo.CreateAsync(new AgentRoleCreate($"r-{Guid.NewGuid():N}", "R", null, null));
        await repo.ReplaceGrantsAsync(roleId,
        [
            new AgentRoleToolGrant(AgentRoleToolCategory.Host, "echo"),
        ]);
        await repo.ReplaceAssignmentsForLatestAsync(agentKey, new[] { roleId });

        // Cascade is on FK; hard-delete the row and verify children are gone.
        var entity = await context.AgentRoles.SingleAsync(r => r.Id == roleId);
        context.AgentRoles.Remove(entity);
        await context.SaveChangesAsync();

        var orphanGrants = await context.AgentRoleToolGrants.CountAsync(g => g.RoleId == roleId);
        var orphanAssignments = await context.AgentRoleAssignments.CountAsync(a => a.RoleId == roleId);
        orphanGrants.Should().Be(0);
        orphanAssignments.Should().Be(0);
    }

    [Fact]
    public async Task ArchiveAsync_marks_role_archived_and_hides_from_default_list()
    {
        var key = $"role-{Guid.NewGuid():N}";

        await using var context = CreateDbContext();
        var repo = new AgentRoleRepository(context);

        var id = await repo.CreateAsync(new AgentRoleCreate(key, "R", null, null));
        await repo.ArchiveAsync(id);

        (await repo.ListAsync(includeArchived: false)).Should().NotContain(r => r.Id == id);
        (await repo.ListAsync(includeArchived: true)).Should().Contain(r => r.Id == id);
    }

    [Fact]
    public async Task UpdateAsync_throws_AgentRoleNotFoundException_for_unknown_id()
    {
        await using var context = CreateDbContext();
        var repo = new AgentRoleRepository(context);

        var act = () => repo.UpdateAsync(99999, new AgentRoleUpdate("x", null, null));

        await act.Should().ThrowAsync<AgentRoleNotFoundException>();
    }

    [Fact]
    public async Task GetRolesForAgentAsync_filters_by_specific_agent_version()
    {
        var agentKey = $"agent-{Guid.NewGuid():N}";

        await using var ctx = CreateDbContext();
        var repo = new AgentRoleRepository(ctx);

        var roleA = await repo.CreateAsync(new AgentRoleCreate($"role-a-{Guid.NewGuid():N}", "A", null, null));
        var roleB = await repo.CreateAsync(new AgentRoleCreate($"role-b-{Guid.NewGuid():N}", "B", null, null));

        // Seed assignment rows directly so we control the agent_version values. AR-1 backfill
        // produces equivalent rows naturally; we replicate that shape here without leaning on
        // AR-4's eventual bump-on-write writer.
        var nowUtc = DateTime.UtcNow;
        ctx.AgentRoleAssignments.AddRange(
            new AgentRoleAssignmentEntity { AgentKey = agentKey, AgentVersion = 1, RoleId = roleA, CreatedAtUtc = nowUtc },
            new AgentRoleAssignmentEntity { AgentKey = agentKey, AgentVersion = 2, RoleId = roleB, CreatedAtUtc = nowUtc });
        await ctx.SaveChangesAsync();

        (await repo.GetRolesForAgentAsync(agentKey, agentVersion: 1))
            .Select(r => r.Id).Should().Equal(roleA);
        (await repo.GetRolesForAgentAsync(agentKey, agentVersion: 2))
            .Select(r => r.Id).Should().Equal(roleB);
        (await repo.GetRolesForAgentAsync(agentKey, agentVersion: 99))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task GetRolesForAgentLatestAsync_returns_assignments_at_max_agent_version()
    {
        var agentKey = $"agent-{Guid.NewGuid():N}";

        await using var ctx = CreateDbContext();
        var repo = new AgentRoleRepository(ctx);
        var agentRepo = new AgentConfigRepository(ctx);

        var roleA = await repo.CreateAsync(new AgentRoleCreate($"role-a-{Guid.NewGuid():N}", "A", null, null));
        var roleB = await repo.CreateAsync(new AgentRoleCreate($"role-b-{Guid.NewGuid():N}", "B", null, null));

        // Seed agents at v1, v2, v3. "Latest" anchors on the agents table — a fresh bump
        // that clears the assignment lands a v3 row with no assignment, and the reader
        // must surface that as empty (not fall back to a stale v2 assignment).
        var configJson = """{"type":"agent","provider":"openai","model":"x"}""";
        await agentRepo.CreateNewVersionAsync(agentKey, configJson, "tester");
        await agentRepo.CreateNewVersionAsync(agentKey, configJson, "tester");
        await agentRepo.CreateNewVersionAsync(agentKey, configJson, "tester");

        var nowUtc = DateTime.UtcNow;
        ctx.AgentRoleAssignments.AddRange(
            new AgentRoleAssignmentEntity { AgentKey = agentKey, AgentVersion = 1, RoleId = roleA, CreatedAtUtc = nowUtc },
            new AgentRoleAssignmentEntity { AgentKey = agentKey, AgentVersion = 3, RoleId = roleB, CreatedAtUtc = nowUtc });
        await ctx.SaveChangesAsync();

        var latest = await repo.GetRolesForAgentLatestAsync(agentKey);
        latest.Select(r => r.Id).Should().Equal(roleB);

        // No rows for an unrelated key → empty list (orphan path), not exception.
        (await repo.GetRolesForAgentLatestAsync($"missing-{Guid.NewGuid():N}"))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task GetRolesForAgentLatestAsync_returns_empty_when_latest_agent_version_has_no_rows()
    {
        // sc-828 / AR-4: bump-on-write that clears the assignment lands a v_N+1 agents row
        // with NO assignment_rows entries. Reader must anchor "latest" on agents.version,
        // not on the highest agent_version in agent_role_assignments — otherwise the stale
        // v_N assignment would shadow the cleared v_N+1.
        var agentKey = $"agent-{Guid.NewGuid():N}";

        await using var ctx = CreateDbContext();
        var repo = new AgentRoleRepository(ctx);
        var agentRepo = new AgentConfigRepository(ctx);

        var role = await repo.CreateAsync(new AgentRoleCreate($"role-{Guid.NewGuid():N}", "R", null, null));

        var configJson = """{"type":"agent","provider":"openai","model":"x"}""";
        await agentRepo.CreateNewVersionAsync(agentKey, configJson, "tester");
        await agentRepo.CreateNewVersionAsync(agentKey, configJson, "tester");

        // Only v1 has an assignment row; v2 (the latest) has none.
        ctx.AgentRoleAssignments.Add(new AgentRoleAssignmentEntity
        {
            AgentKey = agentKey,
            AgentVersion = 1,
            RoleId = role,
            CreatedAtUtc = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();

        var latest = await repo.GetRolesForAgentLatestAsync(agentKey);
        latest.Should().BeEmpty("agents.max_version is 2 and v2 has no assignment rows");
    }

    [Fact]
    public async Task BumpAgentForRoleAssignmentChangeAsync_creates_new_version_with_same_body_and_new_assignment()
    {
        var agentKey = $"agent-{Guid.NewGuid():N}";

        await using var ctx = CreateDbContext();
        var roleRepo = new AgentRoleRepository(ctx);
        var agentRepo = new AgentConfigRepository(ctx);

        var roleA = await roleRepo.CreateAsync(new AgentRoleCreate($"role-a-{Guid.NewGuid():N}", "A", null, null));
        var roleB = await roleRepo.CreateAsync(new AgentRoleCreate($"role-b-{Guid.NewGuid():N}", "B", null, null));

        // Seed agent v1 with role-a via in-place writer.
        var configJson = """{"type":"agent","provider":"openai","model":"gpt-test"}""";
        var v1 = await agentRepo.CreateNewVersionAsync(agentKey, configJson, "tester");
        v1.Should().Be(1);
        await roleRepo.ReplaceAssignmentsAsync(agentKey, agentVersion: 1, new[] { roleA });

        // Bump: same body, new assignment (role-b).
        var newVersion = await roleRepo.BumpAgentForRoleAssignmentChangeAsync(
            agentKey, new[] { roleB }, expectedFromVersion: 1, createdBy: "tester");

        newVersion.Should().Be(2);

        var v2Entity = await ctx.Agents.AsNoTracking().SingleAsync(a => a.Key == agentKey && a.Version == 2);
        v2Entity.ConfigJson.Should().Be(configJson, "bump clones the latest body verbatim");
        v2Entity.IsActive.Should().BeTrue("the new version is the active one");

        // v1 stays as-is, just no longer active.
        var v1Entity = await ctx.Agents.AsNoTracking().SingleAsync(a => a.Key == agentKey && a.Version == 1);
        v1Entity.IsActive.Should().BeFalse();

        // Assignments are scoped per version: v1 keeps role-a, v2 carries role-b.
        (await roleRepo.GetRolesForAgentAsync(agentKey, 1))
            .Select(r => r.Id).Should().Equal(roleA);
        (await roleRepo.GetRolesForAgentAsync(agentKey, 2))
            .Select(r => r.Id).Should().Equal(roleB);
    }

    [Fact]
    public async Task BumpAgentForRoleAssignmentChangeAsync_throws_drift_when_expected_version_is_stale()
    {
        var agentKey = $"agent-{Guid.NewGuid():N}";

        await using var ctx = CreateDbContext();
        var roleRepo = new AgentRoleRepository(ctx);
        var agentRepo = new AgentConfigRepository(ctx);

        var role = await roleRepo.CreateAsync(new AgentRoleCreate($"role-{Guid.NewGuid():N}", "R", null, null));
        await agentRepo.CreateNewVersionAsync(agentKey, """{"type":"agent","provider":"openai","model":"gpt-test"}""", "tester");
        await agentRepo.CreateNewVersionAsync(agentKey, """{"type":"agent","provider":"openai","model":"gpt-test","systemPrompt":"v2"}""", "tester");

        // Caller previewed against v1 but v2 already exists.
        var act = () => roleRepo.BumpAgentForRoleAssignmentChangeAsync(
            agentKey, new[] { role }, expectedFromVersion: 1, createdBy: "tester");

        var ex = await act.Should().ThrowAsync<AgentConfigVersionDriftException>();
        ex.Which.ExpectedVersion.Should().Be(1);
        ex.Which.ActualVersion.Should().Be(2);

        // Retrying with the actual latest succeeds and lands at v3.
        var v3 = await roleRepo.BumpAgentForRoleAssignmentChangeAsync(
            agentKey, new[] { role }, expectedFromVersion: 2, createdBy: "tester");
        v3.Should().Be(3);
    }

    [Fact]
    public async Task BumpAgentForRoleAssignmentChangeAsync_throws_NotFound_when_agent_does_not_exist()
    {
        var agentKey = $"missing-{Guid.NewGuid():N}";

        await using var ctx = CreateDbContext();
        var roleRepo = new AgentRoleRepository(ctx);

        var role = await roleRepo.CreateAsync(new AgentRoleCreate($"role-{Guid.NewGuid():N}", "R", null, null));

        var act = () => roleRepo.BumpAgentForRoleAssignmentChangeAsync(
            agentKey, new[] { role }, expectedFromVersion: null, createdBy: "tester");

        await act.Should().ThrowAsync<AgentConfigNotFoundException>();
    }

    [Fact]
    public async Task ReplaceAssignmentsForLatestAsync_writes_at_max_existing_agent_version()
    {
        var agentKey = $"agent-{Guid.NewGuid():N}";

        await using var ctx = CreateDbContext();
        var roleRepo = new AgentRoleRepository(ctx);
        var agentRepo = new AgentConfigRepository(ctx);

        var role = await roleRepo.CreateAsync(new AgentRoleCreate($"role-{Guid.NewGuid():N}", "R", null, null));
        await agentRepo.CreateNewVersionAsync(agentKey, """{"type":"agent","provider":"openai","model":"x"}""", "tester");
        await agentRepo.CreateNewVersionAsync(agentKey, """{"type":"agent","provider":"openai","model":"x"}""", "tester");

        await roleRepo.ReplaceAssignmentsForLatestAsync(agentKey, new[] { role });

        // Assignment lands at v2 (the latest), not at v1 or v0.
        (await roleRepo.GetRolesForAgentAsync(agentKey, 1)).Should().BeEmpty();
        (await roleRepo.GetRolesForAgentAsync(agentKey, 2)).Select(r => r.Id).Should().Equal(role);
    }

    private CodeFlowDbContext CreateDbContext()
    {
        var builder = new DbContextOptionsBuilder<CodeFlowDbContext>();
        CodeFlowDbContextOptions.Configure(builder, connectionString!);
        return new CodeFlowDbContext(builder.Options);
    }
}
