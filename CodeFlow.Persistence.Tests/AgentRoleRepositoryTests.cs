using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MariaDb;

namespace CodeFlow.Persistence.Tests;

public sealed class AgentRoleRepositoryTests : IAsyncLifetime
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

        await repo.ReplaceAssignmentsAsync(agentKey, new[] { roleA, roleB });

        var afterFirst = await repo.GetRolesForAgentAsync(agentKey);
        afterFirst.Select(r => r.Id).Should().BeEquivalentTo(new[] { roleA, roleB });

        await repo.ReplaceAssignmentsAsync(agentKey, new[] { roleB, roleC });

        var afterSecond = await repo.GetRolesForAgentAsync(agentKey);
        afterSecond.Select(r => r.Id).Should().BeEquivalentTo(new[] { roleB, roleC });
    }

    [Fact]
    public async Task ReplaceAssignmentsAsync_rejects_unknown_role_ids()
    {
        var agentKey = $"agent-{Guid.NewGuid():N}";

        await using var context = CreateDbContext();
        var repo = new AgentRoleRepository(context);

        var realRole = await repo.CreateAsync(new AgentRoleCreate($"r-{Guid.NewGuid():N}", "R", null, null));

        var act = () => repo.ReplaceAssignmentsAsync(agentKey, new[] { realRole, 999999L });

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
        await repo.ReplaceAssignmentsAsync(agentKey, new[] { roleId });

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

    private CodeFlowDbContext CreateDbContext()
    {
        var builder = new DbContextOptionsBuilder<CodeFlowDbContext>();
        CodeFlowDbContextOptions.Configure(builder, connectionString!);
        return new CodeFlowDbContext(builder.Options);
    }
}
