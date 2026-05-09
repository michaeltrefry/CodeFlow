using Microsoft.EntityFrameworkCore;
using System.Data;

namespace CodeFlow.Persistence;

public sealed class AgentRoleRepository(CodeFlowDbContext dbContext) : IAgentRoleRepository
{
    public async Task<IReadOnlyList<AgentRole>> ListAsync(
        bool includeArchived,
        CancellationToken cancellationToken = default)
    {
        return await ListAsync(includeArchived, includeRetired: false, cancellationToken);
    }

    public async Task<IReadOnlyList<AgentRole>> ListAsync(
        bool includeArchived,
        bool includeRetired,
        CancellationToken cancellationToken = default)
    {
        var query = dbContext.AgentRoles.AsNoTracking();
        if (!includeArchived)
        {
            query = query.Where(role => !role.IsArchived);
        }
        if (!includeRetired)
        {
            query = query.Where(role => !role.IsRetired);
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
            TagsJson = WorkflowJson.SerializeTags(TagNormalizer.Normalize(create.Tags)),
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
        if (update.Tags is not null)
        {
            entity.TagsJson = WorkflowJson.SerializeTags(TagNormalizer.Normalize(update.Tags));
        }
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

    public async Task RetireAsync(long id, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.AgentRoles
            .SingleOrDefaultAsync(role => role.Id == id, cancellationToken)
            ?? throw new AgentRoleNotFoundException(id);

        if (!entity.IsRetired)
        {
            entity.IsRetired = true;
            entity.UpdatedAtUtc = DateTime.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<long>> RetireManyAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var distinctIds = ids
            .Where(id => id > 0)
            .Distinct()
            .ToArray();

        if (distinctIds.Length == 0)
        {
            return Array.Empty<long>();
        }

        var entities = await dbContext.AgentRoles
            .Where(role => distinctIds.Contains(role.Id))
            .ToListAsync(cancellationToken);

        if (entities.Count == 0)
        {
            return Array.Empty<long>();
        }

        var changed = false;
        var now = DateTime.UtcNow;
        foreach (var entity in entities)
        {
            if (!entity.IsRetired)
            {
                entity.IsRetired = true;
                entity.UpdatedAtUtc = now;
                changed = true;
            }
        }

        if (changed)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return entities
            .Select(entity => entity.Id)
            .OrderBy(id => id)
            .ToArray();
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

        var roleExists = await dbContext.AgentRoles
            .AsNoTracking()
            .AnyAsync(r => r.Id == id, cancellationToken);
        if (!roleExists) throw new AgentRoleNotFoundException(id);

        var strategy = dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            dbContext.ChangeTracker.Clear();

            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);

            var role = await dbContext.AgentRoles.SingleAsync(r => r.Id == id, cancellationToken);

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
        });
    }

    public async Task<IReadOnlyList<AgentRole>> GetRolesForAgentAsync(
        string agentKey,
        int agentVersion,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeKey(agentKey);

        var roles = await dbContext.AgentRoleAssignments
            .AsNoTracking()
            .Where(assignment =>
                assignment.AgentKey == normalized
                && assignment.AgentVersion == agentVersion)
            .Select(assignment => assignment.Role)
            .OrderBy(role => role.Key)
            .ToListAsync(cancellationToken);

        return roles.Select(Map).ToArray();
    }

    public async Task<IReadOnlyList<AgentRole>> GetRolesForAgentLatestAsync(
        string agentKey,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeKey(agentKey);

        // "Latest" = assignments at the highest existing agents.version for this key. After
        // AR-4 a bump-on-write that clears the assignment lands at v_N+1 with NO assignment
        // rows; reading max(agent_version) off agent_role_assignments would return v_N's
        // (now-stale) row instead. Anchor on the agents table — the authoritative version
        // source — so a freshly-cleared assignment surfaces as an empty list.
        var latestAgentVersion = await dbContext.Agents
            .AsNoTracking()
            .Where(agent => agent.Key == normalized)
            .Select(agent => (int?)agent.Version)
            .MaxAsync(cancellationToken);

        if (latestAgentVersion is null)
        {
            // No agents row → orphan key. Fall back to whatever placeholder rows exist in
            // assignments at v=0; matches pre-AR-4 fixture seeding for tests that exercise
            // the run-time resolution path on orphan keys.
            return await GetRolesForAgentAsync(normalized, agentVersion: 0, cancellationToken);
        }

        return await GetRolesForAgentAsync(normalized, latestAgentVersion.Value, cancellationToken);
    }

    public async Task ReplaceAssignmentsAsync(
        string agentKey,
        int agentVersion,
        IReadOnlyList<long> roleIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roleIds);

        var normalized = NormalizeKey(agentKey);
        var distinctRoleIds = await ValidateRoleIdsAsync(roleIds, cancellationToken);

        var strategy = dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            dbContext.ChangeTracker.Clear();

            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);

            await ReplaceAssignmentsInTransactionAsync(
                normalized,
                agentVersion,
                distinctRoleIds,
                cancellationToken);

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });
    }

    public async Task ReplaceAssignmentsForLatestAsync(
        string agentKey,
        IReadOnlyList<long> roleIds,
        CancellationToken cancellationToken = default)
    {
        // sc-828 / AR-4: explicit "edit the current version's assignment in place" path.
        // Bumping is the default (BumpAgentForRoleAssignmentChangeAsync); callers reach for
        // this only when they want the existing latest agent row to keep its identity (e.g.
        // template materializers seeding both the agent and its assignment at v1, or tests
        // that don't care which version the row lands at). Falls back to agent_version=0 for
        // orphan keys with no agents row — matches the legacy placeholder shape so the
        // standard test fixtures keep behaving the same.
        ArgumentNullException.ThrowIfNull(roleIds);

        var normalized = NormalizeKey(agentKey);
        var distinctRoleIds = await ValidateRoleIdsAsync(roleIds, cancellationToken);

        var strategy = dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            dbContext.ChangeTracker.Clear();

            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);

            var latestVersion = await dbContext.Agents
                .AsNoTracking()
                .Where(agent => agent.Key == normalized)
                .Select(agent => (int?)agent.Version)
                .MaxAsync(cancellationToken);

            await ReplaceAssignmentsInTransactionAsync(
                normalized,
                latestVersion ?? 0,
                distinctRoleIds,
                cancellationToken);

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });
    }

    public async Task<int> BumpAgentForRoleAssignmentChangeAsync(
        string agentKey,
        IReadOnlyList<long> roleIds,
        int? expectedFromVersion,
        string? createdBy,
        CancellationToken cancellationToken = default)
    {
        // sc-828 / AR-4: edits to an agent's role assignment produce a new agent version.
        // Body matches the latest existing row; only the assignment slot moves. Workflows
        // pinning the older version retain their old assignment until they're republished
        // against the new version — same model as any other agent edit.
        ArgumentNullException.ThrowIfNull(roleIds);

        var normalized = NormalizeKey(agentKey);
        var normalizedCreatedBy = string.IsNullOrWhiteSpace(createdBy) ? null : createdBy.Trim();
        var distinctRoleIds = await ValidateRoleIdsAsync(roleIds, cancellationToken);

        var strategy = dbContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            dbContext.ChangeTracker.Clear();

            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);

            var existingConfigs = await dbContext.Agents
                .Where(agent => agent.Key == normalized)
                .OrderBy(agent => agent.Version)
                .ToListAsync(cancellationToken);

            if (existingConfigs.Count == 0)
            {
                throw new AgentConfigNotFoundException(normalized);
            }

            var latestConfig = existingConfigs[^1];

            // Drift gate: if the caller previewed against vN and the latest already moved on
            // (someone else edited the agent), 409 the request so the admin UI can refresh
            // and re-confirm. AR-4 mirror of in-place agent edit's publish-back drift gate.
            if (expectedFromVersion is int expected && expected != latestConfig.Version)
            {
                throw new AgentConfigVersionDriftException(
                    normalized,
                    expectedVersion: expected,
                    actualVersion: latestConfig.Version);
            }

            if (latestConfig.IsRetired)
            {
                throw new InvalidOperationException(
                    $"Agent '{normalized}' is retired; bump-on-write rejected.");
            }

            var newVersion = latestConfig.Version + 1;

            foreach (var config in existingConfigs.Where(c => c.IsActive))
            {
                config.IsActive = false;
            }

            var bumped = new AgentConfigEntity
            {
                Key = normalized,
                Version = newVersion,
                ConfigJson = latestConfig.ConfigJson,
                CreatedAtUtc = DateTime.UtcNow,
                CreatedBy = normalizedCreatedBy ?? latestConfig.CreatedBy,
                IsActive = true,
                IsRetired = false,
                OwningWorkflowKey = latestConfig.OwningWorkflowKey,
                ForkedFromKey = latestConfig.ForkedFromKey,
                ForkedFromVersion = latestConfig.ForkedFromVersion,
                TagsJson = latestConfig.TagsJson,
            };
            dbContext.Agents.Add(bumped);

            await ReplaceAssignmentsInTransactionAsync(
                normalized,
                newVersion,
                distinctRoleIds,
                cancellationToken);

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return newVersion;
        });
    }

    private async Task<long[]> ValidateRoleIdsAsync(
        IReadOnlyList<long> roleIds,
        CancellationToken cancellationToken)
    {
        var distinctRoleIds = roleIds.Distinct().ToArray();
        if (distinctRoleIds.Length == 0)
        {
            return distinctRoleIds;
        }

        var validIds = await dbContext.AgentRoles
            .Where(role => distinctRoleIds.Contains(role.Id) && !role.IsArchived && !role.IsRetired)
            .Select(role => role.Id)
            .ToListAsync(cancellationToken);

        var missing = distinctRoleIds.Except(validIds).ToArray();
        if (missing.Length > 0)
        {
            throw new AgentRoleNotFoundException(missing[0]);
        }

        return distinctRoleIds;
    }

    private async Task ReplaceAssignmentsInTransactionAsync(
        string normalizedAgentKey,
        int agentVersion,
        IReadOnlyList<long> distinctRoleIds,
        CancellationToken cancellationToken)
    {
        var existing = await dbContext.AgentRoleAssignments
            .Where(assignment =>
                assignment.AgentKey == normalizedAgentKey
                && assignment.AgentVersion == agentVersion)
            .ToListAsync(cancellationToken);

        dbContext.AgentRoleAssignments.RemoveRange(existing);

        var now = DateTime.UtcNow;
        foreach (var roleId in distinctRoleIds)
        {
            dbContext.AgentRoleAssignments.Add(new AgentRoleAssignmentEntity
            {
                AgentKey = normalizedAgentKey,
                AgentVersion = agentVersion,
                RoleId = roleId,
                CreatedAtUtc = now,
            });
        }
    }

    public async Task<IReadOnlyList<long>> GetSkillGrantsAsync(long id, CancellationToken cancellationToken = default)
    {
        var skillIds = await dbContext.AgentRoleSkillGrants
            .AsNoTracking()
            .Where(grant => grant.RoleId == id)
            .OrderBy(grant => grant.SkillId)
            .Select(grant => grant.SkillId)
            .ToListAsync(cancellationToken);

        return skillIds;
    }

    public async Task ReplaceSkillGrantsAsync(long id, IReadOnlyList<long> skillIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(skillIds);

        var roleExists = await dbContext.AgentRoles
            .AsNoTracking()
            .AnyAsync(r => r.Id == id, cancellationToken);
        if (!roleExists) throw new AgentRoleNotFoundException(id);

        var distinctSkillIds = skillIds.Distinct().ToArray();

        if (distinctSkillIds.Length > 0)
        {
            var validIds = await dbContext.Skills
                .AsNoTracking()
                .Where(skill => distinctSkillIds.Contains(skill.Id))
                .Select(skill => skill.Id)
                .ToListAsync(cancellationToken);

            var missing = distinctSkillIds.Except(validIds).ToArray();
            if (missing.Length > 0)
            {
                throw new SkillNotFoundException(missing[0]);
            }
        }

        var strategy = dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            dbContext.ChangeTracker.Clear();

            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);

            var role = await dbContext.AgentRoles.SingleAsync(r => r.Id == id, cancellationToken);

            var existing = await dbContext.AgentRoleSkillGrants
                .Where(grant => grant.RoleId == id)
                .ToListAsync(cancellationToken);

            dbContext.AgentRoleSkillGrants.RemoveRange(existing);

            foreach (var skillId in distinctSkillIds)
            {
                dbContext.AgentRoleSkillGrants.Add(new AgentRoleSkillGrantEntity
                {
                    RoleId = id,
                    SkillId = skillId,
                });
            }

            role.UpdatedAtUtc = DateTime.UtcNow;

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });
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
        IsArchived: entity.IsArchived,
        IsRetired: entity.IsRetired,
        IsSystemManaged: entity.IsSystemManaged,
        Tags: TagNormalizer.Normalize(WorkflowJson.DeserializeTags(entity.TagsJson)));

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
