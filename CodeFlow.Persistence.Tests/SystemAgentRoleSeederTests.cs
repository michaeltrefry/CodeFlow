using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MariaDb;

namespace CodeFlow.Persistence.Tests;

public sealed class SystemAgentRoleSeederTests : IAsyncLifetime
{
    private readonly MariaDbContainer mariaDbContainer = new MariaDbBuilder("mariadb:11.4")
        .WithDatabase("codeflow_seed_tests")
        .WithUsername("codeflow")
        .WithPassword("codeflow_dev")
        .Build();

    private string? connectionString;

    public async Task InitializeAsync()
    {
        await mariaDbContainer.StartAsync();
        connectionString = mariaDbContainer.GetConnectionString();

        await using var ctx = CreateDbContext();
        await ctx.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await mariaDbContainer.DisposeAsync();
    }

    [Fact]
    public async Task SeedAsync_FreshDb_InsertsAllSystemRolesWithCatalogGrants()
    {
        // S1: a fresh install should land all system roles + their full grant catalog,
        // each marked IsSystemManaged so the API gates protect them.
        await using var ctx = CreateDbContext();

        await SystemAgentRoleSeeder.SeedAsync(ctx);

        var seeded = await ctx.AgentRoles
            .AsNoTracking()
            .Where(r => SystemAgentRoles.All.Select(sr => sr.Key).Contains(r.Key))
            .OrderBy(r => r.Key)
            .ToListAsync();
        seeded.Should().HaveCount(SystemAgentRoles.All.Count);
        seeded.Should().AllSatisfy(r => r.IsSystemManaged.Should().BeTrue());
        seeded.Should().AllSatisfy(r => r.IsArchived.Should().BeFalse());

        var codeWorker = seeded.Single(r => r.Key == SystemAgentRoles.CodeWorkerKey);
        var grants = await ctx.AgentRoleToolGrants
            .AsNoTracking()
            .Where(g => g.RoleId == codeWorker.Id)
            .ToListAsync();
        grants.Select(g => g.ToolIdentifier)
            .Should().BeEquivalentTo("read_file", "apply_patch", "run_command", "echo", "now");
        grants.Should().AllSatisfy(g => g.Category.Should().Be(AgentRoleToolCategory.Host));

        var readOnlyShell = seeded.Single(r => r.Key == SystemAgentRoles.ReadOnlyShellKey);
        var readOnlyGrants = await ctx.AgentRoleToolGrants
            .AsNoTracking()
            .Where(g => g.RoleId == readOnlyShell.Id)
            .Select(g => g.ToolIdentifier)
            .ToListAsync();
        readOnlyGrants.Should().BeEquivalentTo("read_file", "echo", "now");
        readOnlyGrants.Should().NotContain("run_command");
    }

    [Fact]
    public async Task SeedAsync_CodeBuilderRole_GrantsContainerAndWebToolsOnTopOfCodeWorker()
    {
        // sc-452: code-builder is the only seeded role with container/web grants. Its
        // description must explicitly forbid Dockerfile builds so operators see the policy
        // before they assign it; the grant set is the full code-worker bundle plus
        // container.run + web_fetch + web_search.
        await using var ctx = CreateDbContext();

        await SystemAgentRoleSeeder.SeedAsync(ctx);

        var codeBuilder = await ctx.AgentRoles
            .AsNoTracking()
            .SingleAsync(r => r.Key == SystemAgentRoles.CodeBuilderKey);
        codeBuilder.IsSystemManaged.Should().BeTrue();
        codeBuilder.Description.Should().Contain("docker.io");
        codeBuilder.Description.Should().Contain("no repo Dockerfiles");

        var grants = await ctx.AgentRoleToolGrants
            .AsNoTracking()
            .Where(g => g.RoleId == codeBuilder.Id)
            .ToListAsync();
        grants.Select(g => g.ToolIdentifier)
            .Should().BeEquivalentTo(
                "read_file",
                "apply_patch",
                "run_command",
                "container.run",
                "web_fetch",
                "web_search",
                "echo",
                "now");
        grants.Should().AllSatisfy(g => g.Category.Should().Be(AgentRoleToolCategory.Host));
    }

    [Fact]
    public async Task SeedAsync_CodeWorkerRole_DoesNotIncludeContainerOrWebTools()
    {
        // sc-452: container.run/web_fetch/web_search are opt-in via code-builder so
        // existing fresh-install operators don't get unbounded outbound network or Docker
        // execution silently added to the default dev role. Operators who want them switch
        // their dev agents to code-builder explicitly.
        await using var ctx = CreateDbContext();

        await SystemAgentRoleSeeder.SeedAsync(ctx);

        var codeWorker = await ctx.AgentRoles
            .AsNoTracking()
            .SingleAsync(r => r.Key == SystemAgentRoles.CodeWorkerKey);
        var identifiers = await ctx.AgentRoleToolGrants
            .AsNoTracking()
            .Where(g => g.RoleId == codeWorker.Id)
            .Select(g => g.ToolIdentifier)
            .ToListAsync();
        identifiers.Should().NotContain("container.run");
        identifiers.Should().NotContain("web_fetch");
        identifiers.Should().NotContain("web_search");
    }

    [Fact]
    public async Task SeedAsync_KanbanRole_GrantsMcpToolsUnderConventionalServerKey()
    {
        await using var ctx = CreateDbContext();

        await SystemAgentRoleSeeder.SeedAsync(ctx);

        var kanban = await ctx.AgentRoles
            .AsNoTracking()
            .SingleAsync(r => r.Key == SystemAgentRoles.KanbanWorkerKey);
        kanban.IsSystemManaged.Should().BeTrue();

        var grants = await ctx.AgentRoleToolGrants
            .AsNoTracking()
            .Where(g => g.RoleId == kanban.Id)
            .ToListAsync();
        grants.Should().NotBeEmpty();
        grants.Should().AllSatisfy(g =>
        {
            g.Category.Should().Be(AgentRoleToolCategory.Mcp);
            g.ToolIdentifier.Should().StartWith($"mcp:{SystemAgentRoles.KanbanMcpServerKey}:");
        });
    }

    [Fact]
    public async Task SeedAsync_RunTwice_IsIdempotent_NoExtraRowsOrGrants()
    {
        // Seeder is invoked on every host startup; a second run must be a no-op for both
        // the role rows and their grant child tables.
        await using var ctx = CreateDbContext();

        await SystemAgentRoleSeeder.SeedAsync(ctx);
        var rolesAfterFirst = await ctx.AgentRoles.AsNoTracking().CountAsync();
        var grantsAfterFirst = await ctx.AgentRoleToolGrants.AsNoTracking().CountAsync();

        await SystemAgentRoleSeeder.SeedAsync(ctx);
        var rolesAfterSecond = await ctx.AgentRoles.AsNoTracking().CountAsync();
        var grantsAfterSecond = await ctx.AgentRoleToolGrants.AsNoTracking().CountAsync();

        rolesAfterSecond.Should().Be(rolesAfterFirst);
        grantsAfterSecond.Should().Be(grantsAfterFirst);
    }

    [Fact]
    public async Task SeedAsync_OperatorRemovedGrant_IsNotReAdded()
    {
        // Insert-only contract: once a system role exists, the operator owns it. If they
        // delete a seeded grant, the next startup must leave their edit alone rather than
        // re-syncing from the catalog.
        await using var ctx = CreateDbContext();

        await SystemAgentRoleSeeder.SeedAsync(ctx);

        var role = await ctx.AgentRoles
            .SingleAsync(r => r.Key == SystemAgentRoles.CodeWorkerKey);
        var droppedGrant = await ctx.AgentRoleToolGrants
            .Where(g => g.RoleId == role.Id && g.ToolIdentifier == "echo")
            .SingleAsync();
        ctx.AgentRoleToolGrants.Remove(droppedGrant);
        await ctx.SaveChangesAsync();

        await SystemAgentRoleSeeder.SeedAsync(ctx);

        var grantsAfter = await ctx.AgentRoleToolGrants
            .AsNoTracking()
            .Where(g => g.RoleId == role.Id)
            .Select(g => g.ToolIdentifier)
            .ToListAsync();
        grantsAfter.Should().NotContain("echo");
        grantsAfter.Should().BeEquivalentTo("read_file", "apply_patch", "run_command", "now");
    }

    [Fact]
    public async Task SeedAsync_ExistingNonSystemRoleAtSameKey_IsLeftAlone()
    {
        // Documented collision strategy: an operator's pre-existing role at the same key
        // wins. The platform variant is not seeded; the operator can rename and re-run if
        // they want the system role.
        await using var ctx = CreateDbContext();
        var repo = new AgentRoleRepository(ctx);

        var operatorRoleId = await repo.CreateAsync(new AgentRoleCreate(
            Key: SystemAgentRoles.CodeWorkerKey,
            DisplayName: "Operator's custom code-worker",
            Description: "Pre-existing role; the seeder must not clobber it.",
            CreatedBy: "operator"));

        await SystemAgentRoleSeeder.SeedAsync(ctx);

        await using var verifyCtx = CreateDbContext();
        var preserved = await verifyCtx.AgentRoles
            .AsNoTracking()
            .SingleAsync(r => r.Key == SystemAgentRoles.CodeWorkerKey);
        preserved.Id.Should().Be(operatorRoleId);
        preserved.DisplayName.Should().Be("Operator's custom code-worker");
        preserved.IsSystemManaged.Should().BeFalse();
        preserved.CreatedBy.Should().Be("operator");

        // Operator's role has no auto-seeded grants.
        var grants = await verifyCtx.AgentRoleToolGrants
            .AsNoTracking()
            .Where(g => g.RoleId == operatorRoleId)
            .ToListAsync();
        grants.Should().BeEmpty();
    }

    private CodeFlowDbContext CreateDbContext()
    {
        var builder = new DbContextOptionsBuilder<CodeFlowDbContext>();
        CodeFlowDbContextOptions.Configure(builder, connectionString!);
        return new CodeFlowDbContext(builder.Options);
    }
}
