using Microsoft.EntityFrameworkCore;

namespace CodeFlow.Persistence;

/// <summary>
/// First-run seeder for the platform-suggested agent roles listed in
/// <see cref="SystemAgentRoles.All"/>. Safe to run on every startup, but only inserts roles
/// (and their grants) that don't already exist in the database. Once a system role is in the
/// database, the operator owns it — the seeder will not overwrite display name, description,
/// or grants on subsequent runs. <see cref="AgentRoleEntity.IsSystemManaged"/> stays set so a
/// future admin-only gate can still distinguish seeded roles from operator-created ones.
/// </summary>
public static class SystemAgentRoleSeeder
{
    public static async Task SeedAsync(CodeFlowDbContext db, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var seedKeys = SystemAgentRoles.All.Select(r => r.Key).ToArray();
        var existingKeys = await db.AgentRoles
            .Where(r => seedKeys.Contains(r.Key))
            .Select(r => r.Key)
            .ToListAsync(cancellationToken);
        var existingKeySet = new HashSet<string>(existingKeys, StringComparer.Ordinal);

        var now = DateTime.UtcNow;

        foreach (var systemRole in SystemAgentRoles.All)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (existingKeySet.Contains(systemRole.Key))
            {
                continue;
            }

            var entity = new AgentRoleEntity
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

            foreach (var grant in systemRole.Grants)
            {
                db.AgentRoleToolGrants.Add(new AgentRoleToolGrantEntity
                {
                    RoleId = entity.Id,
                    Category = grant.Category,
                    ToolIdentifier = grant.ToolIdentifier,
                });
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
