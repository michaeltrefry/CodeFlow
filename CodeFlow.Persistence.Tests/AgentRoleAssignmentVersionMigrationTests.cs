using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;

namespace CodeFlow.Persistence.Tests;

/// <summary>
/// Migration-correctness tests for <c>AddAgentRoleAssignmentVersion</c> (sc-825). Validates
/// the up-migration's backfill replication and the down-migration's row collapse against a
/// real MariaDB schema. The other repository tests assert post-migration runtime behaviour;
/// this class is the only one that exercises the up/down SQL with seeded data on either side.
/// </summary>
[Collection(PersistenceMariaDbCollection.Name)]
public sealed class AgentRoleAssignmentVersionMigrationTests : IAsyncLifetime
{
    private const string MigrationName = "20260509170259_AddAgentRoleAssignmentVersion";
    private const string PreviousMigrationName = "20260506114005_AddAssistantArtifactEvents";
    private const string DatabaseName = "test_agentroleassignmentversionmigrationtests";

    private readonly SharedMariaDbFixture mariaDb;
    private string? connectionString;

    public AgentRoleAssignmentVersionMigrationTests(SharedMariaDbFixture mariaDb)
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
    public async Task Up_replicates_pre_migration_assignments_across_every_agent_version()
    {
        var agentKey = $"agent-{Guid.NewGuid():N}";
        var orphanKey = $"orphan-{Guid.NewGuid():N}";

        // Seed a role we can attach assignments to. The role lives in agent_roles which is
        // version-agnostic, so it survives the up/down dance untouched.
        long roleId;
        await using (var seedCtx = CreateDbContext())
        {
            var role = new AgentRoleEntity
            {
                Key = $"role-{Guid.NewGuid():N}",
                DisplayName = "Migration Test Role",
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
                TagsJson = "[]",
            };
            seedCtx.AgentRoles.Add(role);
            await seedCtx.SaveChangesAsync();
            roleId = role.Id;
        }

        // Two versions of the same agent_key plus an orphan key with no agents row. After
        // migration we expect: replicas at each agents.version for agentKey, and a single
        // agent_version=0 placeholder for orphanKey (orphan assignments preserve current
        // behaviour even when their agent_key never existed in agents).
        await using (var seedCtx = CreateDbContext())
        {
            seedCtx.Agents.Add(new AgentConfigEntity
            {
                Key = agentKey,
                Version = 1,
                ConfigJson = "{}",
                CreatedAtUtc = DateTime.UtcNow,
                IsActive = false,
            });
            seedCtx.Agents.Add(new AgentConfigEntity
            {
                Key = agentKey,
                Version = 2,
                ConfigJson = "{}",
                CreatedAtUtc = DateTime.UtcNow,
                IsActive = true,
            });
            await seedCtx.SaveChangesAsync();
        }

        // Roll back to the schema BEFORE this migration so we can write rows in the old
        // (id, agent_key, role_id) shape with no agent_version column. This is the only
        // way to exercise the up-migration's backfill SQL against real pre-migration data.
        await MigrateToAsync(PreviousMigrationName);

        await using (var connection = new MySqlConnection(connectionString))
        {
            await connection.OpenAsync();

            await using (var insert = connection.CreateCommand())
            {
                insert.CommandText = @"
                    INSERT INTO agent_role_assignments (agent_key, role_id, created_at)
                    VALUES (@agentKey, @roleId, UTC_TIMESTAMP(6)),
                           (@orphanKey, @roleId, UTC_TIMESTAMP(6));";
                insert.Parameters.AddWithValue("@agentKey", agentKey);
                insert.Parameters.AddWithValue("@orphanKey", orphanKey);
                insert.Parameters.AddWithValue("@roleId", roleId);
                await insert.ExecuteNonQueryAsync();
            }
        }

        // Roll forward through the up-migration: backfill should replicate the agentKey row
        // across versions 1 and 2, drop the placeholder, and leave the orphan placeholder
        // alone at agent_version=0.
        await MigrateToAsync(MigrationName);

        await using (var verifyCtx = CreateDbContext())
        {
            var agentRows = await verifyCtx.AgentRoleAssignments
                .AsNoTracking()
                .Where(a => a.AgentKey == agentKey)
                .OrderBy(a => a.AgentVersion)
                .ToListAsync();

            agentRows.Should().HaveCount(2, "one row per existing agents.version for the agent_key");
            agentRows.Select(a => a.AgentVersion).Should().Equal(1, 2);
            agentRows.Should().OnlyContain(a => a.RoleId == roleId);
            agentRows.Should().OnlyContain(
                a => a.AgentVersion != 0,
                "the placeholder at agent_version=0 must be deleted once replicas exist");

            var orphanRows = await verifyCtx.AgentRoleAssignments
                .AsNoTracking()
                .Where(a => a.AgentKey == orphanKey)
                .ToListAsync();

            orphanRows.Should().HaveCount(1, "orphan assignments (agent_key with no agents row) keep the placeholder");
            orphanRows[0].AgentVersion.Should().Be(0);
            orphanRows[0].RoleId.Should().Be(roleId);
        }
    }

    [Fact]
    public async Task Down_collapses_versioned_rows_to_one_per_agent_role_keeping_max_version()
    {
        var agentKey = $"agent-{Guid.NewGuid():N}";

        long roleId;
        await using (var seedCtx = CreateDbContext())
        {
            var role = new AgentRoleEntity
            {
                Key = $"role-{Guid.NewGuid():N}",
                DisplayName = "Down Migration Test Role",
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
                TagsJson = "[]",
            };
            seedCtx.AgentRoles.Add(role);
            await seedCtx.SaveChangesAsync();
            roleId = role.Id;
        }

        // Seed three rows that mimic post-migration replication: same (agent_key, role_id)
        // across versions 1, 2, 3. Down-migration should collapse to one row at version 3.
        await using (var seedCtx = CreateDbContext())
        {
            seedCtx.AgentRoleAssignments.AddRange(
                new AgentRoleAssignmentEntity { AgentKey = agentKey, AgentVersion = 1, RoleId = roleId, CreatedAtUtc = DateTime.UtcNow },
                new AgentRoleAssignmentEntity { AgentKey = agentKey, AgentVersion = 2, RoleId = roleId, CreatedAtUtc = DateTime.UtcNow },
                new AgentRoleAssignmentEntity { AgentKey = agentKey, AgentVersion = 3, RoleId = roleId, CreatedAtUtc = DateTime.UtcNow });
            await seedCtx.SaveChangesAsync();
        }

        await MigrateToAsync(PreviousMigrationName);

        // After down-migration the agent_version column is gone and only one row remains
        // for (agent_key, role_id). We verify via raw SQL because the entity model post-down
        // expects a column shape that no longer matches the table.
        await using (var connection = new MySqlConnection(connectionString))
        {
            await connection.OpenAsync();

            await using var count = connection.CreateCommand();
            count.CommandText = @"
                SELECT COUNT(*) FROM agent_role_assignments
                WHERE agent_key = @agentKey AND role_id = @roleId;";
            count.Parameters.AddWithValue("@agentKey", agentKey);
            count.Parameters.AddWithValue("@roleId", roleId);

            var result = await count.ExecuteScalarAsync();
            Convert.ToInt64(result!).Should().Be(1, "down-migration collapses to one row per (agent_key, role_id) keeping MAX(agent_version)");

            await using var columnCheck = connection.CreateCommand();
            columnCheck.CommandText = @"
                SELECT COUNT(*) FROM information_schema.columns
                WHERE table_schema = DATABASE()
                  AND table_name = 'agent_role_assignments'
                  AND column_name = 'agent_version';";
            var columnExists = Convert.ToInt64((await columnCheck.ExecuteScalarAsync())!);
            columnExists.Should().Be(0, "down-migration drops the agent_version column");
        }

        // Roll back forward so the test fixture's drop-database in DisposeAsync sees a known
        // schema. Not strictly required, but keeps cross-test state clean.
        await MigrateToAsync(MigrationName);
    }

    private async Task MigrateToAsync(string targetMigration)
    {
        await using var ctx = CreateDbContext();
        var migrator = ctx.GetInfrastructure().GetRequiredService<IMigrator>();
        await migrator.MigrateAsync(targetMigration);
    }

    private CodeFlowDbContext CreateDbContext()
    {
        var builder = new DbContextOptionsBuilder<CodeFlowDbContext>();
        CodeFlowDbContextOptions.Configure(builder, connectionString!);
        return new CodeFlowDbContext(builder.Options);
    }
}
