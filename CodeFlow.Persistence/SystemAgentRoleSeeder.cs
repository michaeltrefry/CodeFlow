using Microsoft.EntityFrameworkCore;

namespace CodeFlow.Persistence;

/// <summary>
/// S1 (Workflow Authoring DX): idempotently insert / sync the platform-managed agent roles
/// listed in <see cref="SystemAgentRoles.All"/>. Safe to run on every startup.
///
/// Per-role outcomes:
/// <list type="bullet">
///   <item><description>Key not found → insert role + grants (IsSystemManaged = true).</description></item>
///   <item><description>Key found, IsSystemManaged = true → re-sync grants (full replace) so
///   catalog drift (e.g. new host tools shipped in a release) flows automatically.</description></item>
///   <item><description>Key found, IsSystemManaged = false → skip entirely. The operator's
///   custom role of the same name is preserved; the platform variant is not seeded. Documented
///   collision strategy from the requirements doc.</description></item>
/// </list>
/// </summary>
public static class SystemAgentRoleSeeder
{
    public static async Task SeedAsync(CodeFlowDbContext db, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var seedKeys = SystemAgentRoles.All.Select(r => r.Key).ToArray();
        var existing = await db.AgentRoles
            .Where(r => seedKeys.Contains(r.Key))
            .ToDictionaryAsync(r => r.Key, StringComparer.Ordinal, cancellationToken);

        var now = DateTime.UtcNow;

        foreach (var systemRole in SystemAgentRoles.All)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (existing.TryGetValue(systemRole.Key, out var entity))
            {
                if (!entity.IsSystemManaged)
                {
                    // Operator pre-existing role with the same key — leave alone per the
                    // documented collision strategy. The system-managed variant is not seeded.
                    continue;
                }

                if (HasMetadataDrifted(entity, systemRole))
                {
                    entity.DisplayName = systemRole.DisplayName;
                    entity.Description = systemRole.Description;
                    entity.UpdatedAtUtc = now;
                }
            }
            else
            {
                entity = new AgentRoleEntity
                {
                    Key = systemRole.Key,
                    DisplayName = systemRole.DisplayName,
                    Description = systemRole.Description,
                    CreatedAtUtc = now,
                    CreatedBy = null,
                    UpdatedAtUtc = now,
                    UpdatedBy = null,
                    IsArchived = false,
                    IsSystemManaged = true,
                };
                db.AgentRoles.Add(entity);
                await db.SaveChangesAsync(cancellationToken);
            }

            await SyncGrantsAsync(db, entity.Id, systemRole.Grants, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static bool HasMetadataDrifted(AgentRoleEntity entity, SystemAgentRole systemRole) =>
        !string.Equals(entity.DisplayName, systemRole.DisplayName, StringComparison.Ordinal)
        || !string.Equals(entity.Description, systemRole.Description, StringComparison.Ordinal);

    private static async Task SyncGrantsAsync(
        CodeFlowDbContext db,
        long roleId,
        IReadOnlyList<AgentRoleToolGrant> desiredGrants,
        CancellationToken cancellationToken)
    {
        var existing = await db.AgentRoleToolGrants
            .Where(g => g.RoleId == roleId)
            .ToListAsync(cancellationToken);

        var desiredSet = new HashSet<(AgentRoleToolCategory Category, string ToolIdentifier)>(
            desiredGrants.Select(g => (g.Category, g.ToolIdentifier)));

        var existingSet = new HashSet<(AgentRoleToolCategory Category, string ToolIdentifier)>(
            existing.Select(g => (g.Category, g.ToolIdentifier)));

        // Remove grants that are no longer in the catalog (catalog tool retired).
        foreach (var grant in existing)
        {
            if (!desiredSet.Contains((grant.Category, grant.ToolIdentifier)))
            {
                db.AgentRoleToolGrants.Remove(grant);
            }
        }

        // Add grants that the catalog now expects.
        foreach (var grant in desiredGrants)
        {
            if (!existingSet.Contains((grant.Category, grant.ToolIdentifier)))
            {
                db.AgentRoleToolGrants.Add(new AgentRoleToolGrantEntity
                {
                    RoleId = roleId,
                    Category = grant.Category,
                    ToolIdentifier = grant.ToolIdentifier,
                });
            }
        }
    }
}
