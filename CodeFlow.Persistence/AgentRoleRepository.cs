using Microsoft.EntityFrameworkCore;
using System.Data;

namespace CodeFlow.Persistence;

public sealed class AgentRoleRepository(CodeFlowDbContext dbContext) : IAgentRoleRepository
{
    public async Task<IReadOnlyList<AgentRole>> ListAsync(bool includeArchived, CancellationToken cancellationToken = default)
    {
        var query = dbContext.AgentRoles.AsNoTracking();
        if (!includeArchived)
        {
            query = query.Where(role => !role.IsArchived);
        }

        var entities = await query
            .OrderBy(role => role.Key)
            .ToListAsync(cancellationToken);

        return entities.Select(Map).ToArray();
    }

    public async Task<AgentRole?> GetAsync(long id, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.AgentRoles
            .AsNoTracking()
            .SingleOrDefaultAsync(role => role.Id == id, cancellationToken);

        return entity is null ? null : Map(entity);
    }

    public async Task<AgentRole?> GetByKeyAsync(string key, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeKey(key);
        var entity = await dbContext.AgentRoles
            .AsNoTracking()
            .SingleOrDefaultAsync(role => role.Key == normalized, cancellationToken);

        return entity is null ? null : Map(entity);
    }

    public async Task<long> CreateAsync(AgentRoleCreate create, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(create);

        var now = DateTime.UtcNow;
        var entity = new AgentRoleEntity
        {
            Key = NormalizeKey(create.Key),
            DisplayName = Require(create.DisplayName, nameof(create.DisplayName)),
            Description = Trim(create.Description),
            CreatedAtUtc = now,
            CreatedBy = Trim(create.CreatedBy),
            UpdatedAtUtc = now,
            UpdatedBy = Trim(create.CreatedBy),
        };

        dbContext.AgentRoles.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);

        return entity.Id;
    }

    public async Task UpdateAsync(long id, AgentRoleUpdate update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        var entity = await dbContext.AgentRoles
            .SingleOrDefaultAsync(role => role.Id == id, cancellationToken)
            ?? throw new AgentRoleNotFoundException(id);

        entity.DisplayName = Require(update.DisplayName, nameof(update.DisplayName));
        entity.Description = Trim(update.Description);
        entity.UpdatedAtUtc = DateTime.UtcNow;
        entity.UpdatedBy = Trim(update.UpdatedBy);

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task ArchiveAsync(long id, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.AgentRoles
            .SingleOrDefaultAsync(role => role.Id == id, cancellationToken)
            ?? throw new AgentRoleNotFoundException(id);

        if (!entity.IsArchived)
        {
            entity.IsArchived = true;
            entity.UpdatedAtUtc = DateTime.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<AgentRoleToolGrant>> GetGrantsAsync(long id, CancellationToken cancellationToken = default)
    {
        var entities = await dbContext.AgentRoleToolGrants
            .AsNoTracking()
            .Where(grant => grant.RoleId == id)
            .OrderBy(grant => grant.Category)
            .ThenBy(grant => grant.ToolIdentifier)
            .ToListAsync(cancellationToken);

        return entities.Select(e => new AgentRoleToolGrant(e.Category, e.ToolIdentifier)).ToArray();
    }

    public async Task ReplaceGrantsAsync(long id, IReadOnlyList<AgentRoleToolGrant> grants, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grants);

        var role = await dbContext.AgentRoles
            .SingleOrDefaultAsync(r => r.Id == id, cancellationToken)
            ?? throw new AgentRoleNotFoundException(id);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var existing = await dbContext.AgentRoleToolGrants
            .Where(grant => grant.RoleId == id)
            .ToListAsync(cancellationToken);

        dbContext.AgentRoleToolGrants.RemoveRange(existing);

        var deduped = grants
            .GroupBy(g => (g.Category, g.ToolIdentifier))
            .Select(group => group.First());

        foreach (var grant in deduped)
        {
            dbContext.AgentRoleToolGrants.Add(new AgentRoleToolGrantEntity
            {
                RoleId = id,
                Category = grant.Category,
                ToolIdentifier = Require(grant.ToolIdentifier, nameof(grant.ToolIdentifier)),
            });
        }

        role.UpdatedAtUtc = DateTime.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AgentRole>> GetRolesForAgentAsync(string agentKey, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeKey(agentKey);

        var roles = await dbContext.AgentRoleAssignments
            .AsNoTracking()
            .Where(assignment => assignment.AgentKey == normalized)
            .Select(assignment => assignment.Role)
            .OrderBy(role => role.Key)
            .ToListAsync(cancellationToken);

        return roles.Select(Map).ToArray();
    }

    public async Task ReplaceAssignmentsAsync(string agentKey, IReadOnlyList<long> roleIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roleIds);

        var normalized = NormalizeKey(agentKey);
        var distinctRoleIds = roleIds.Distinct().ToArray();

        if (distinctRoleIds.Length > 0)
        {
            var validIds = await dbContext.AgentRoles
                .Where(role => distinctRoleIds.Contains(role.Id))
                .Select(role => role.Id)
                .ToListAsync(cancellationToken);

            var missing = distinctRoleIds.Except(validIds).ToArray();
            if (missing.Length > 0)
            {
                throw new AgentRoleNotFoundException(missing[0]);
            }
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var existing = await dbContext.AgentRoleAssignments
            .Where(assignment => assignment.AgentKey == normalized)
            .ToListAsync(cancellationToken);

        dbContext.AgentRoleAssignments.RemoveRange(existing);

        var now = DateTime.UtcNow;
        foreach (var roleId in distinctRoleIds)
        {
            dbContext.AgentRoleAssignments.Add(new AgentRoleAssignmentEntity
            {
                AgentKey = normalized,
                RoleId = roleId,
                CreatedAtUtc = now,
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static AgentRole Map(AgentRoleEntity entity) => new(
        Id: entity.Id,
        Key: entity.Key,
        DisplayName: entity.DisplayName,
        Description: entity.Description,
        CreatedAtUtc: DateTime.SpecifyKind(entity.CreatedAtUtc, DateTimeKind.Utc),
        CreatedBy: entity.CreatedBy,
        UpdatedAtUtc: DateTime.SpecifyKind(entity.UpdatedAtUtc, DateTimeKind.Utc),
        UpdatedBy: entity.UpdatedBy,
        IsArchived: entity.IsArchived);

    private static string NormalizeKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return key.Trim();
    }

    private static string Require(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        return value.Trim();
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
