using CodeFlow.Runtime;
using Microsoft.EntityFrameworkCore;
using System.Data;

namespace CodeFlow.Persistence;

public sealed class AgentConfigRepository(CodeFlowDbContext dbContext) : IAgentConfigRepository
{
    // Process-wide cache for immutable agent-config versions. Backed by the shared
    // VersionedEntityCache helper (F-006) so the bounded-size + sliding-expiration policy is
    // declared once for every version-pinned repository.
    private static readonly VersionedEntityCache<AgentConfigCacheKey, AgentConfig> Cache =
        new(sizeLimit: 1024, slidingExpiration: TimeSpan.FromMinutes(15));

    /// <summary>
    /// Test-only escape hatch: drop every cached <see cref="AgentConfig"/>. Required because the
    /// cache is process-static and can leak entries between tests that reuse agent keys against
    /// fresh in-memory databases. Production code never calls this.
    /// </summary>
    public static void ClearCacheForTests() => ClearCache();

    public static void ClearCache() => Cache.Clear();

    public async Task<AgentConfig> GetAsync(
        string key,
        int version,
        CancellationToken cancellationToken = default)
    {
        var result = await TryGetAsync(key, version, cancellationToken);
        if (result is null)
        {
            throw new AgentConfigNotFoundException(NormalizeKey(key), version);
        }
        return result;
    }

    public async Task<AgentConfig?> TryGetAsync(
        string key,
        int version,
        CancellationToken cancellationToken = default)
    {
        var normalizedKey = NormalizeKey(key);
        var cacheKey = AgentConfigCacheKey.Create(normalizedKey, version);

        if (Cache.Get(cacheKey) is { } cachedConfig)
        {
            return cachedConfig;
        }

        var entity = await dbContext.Agents
            .AsNoTracking()
            .SingleOrDefaultAsync(
                agent => agent.Key == normalizedKey && agent.Version == version,
                cancellationToken);

        if (entity is null)
        {
            return null;
        }

        var mapped = Map(entity);
        Cache.Set(cacheKey, mapped);
        return mapped;
    }

    public async Task<int> CreateNewVersionAsync(
        string key,
        string configJson,
        string? createdBy,
        CancellationToken cancellationToken = default)
    {
        return await CreateNewVersionAsync(key, configJson, createdBy, tags: null, cancellationToken);
    }

    public async Task<int> CreateNewVersionAsync(
        string key,
        string configJson,
        string? createdBy,
        IReadOnlyList<string>? tags,
        CancellationToken cancellationToken = default)
    {
        var normalizedKey = NormalizeKey(key);
        var normalizedCreatedBy = NormalizeCreatedBy(createdBy);
        var configuration = AgentConfigJson.Deserialize(configJson);

        var strategy = dbContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            dbContext.ChangeTracker.Clear();

            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);

            var existingConfigs = await dbContext.Agents
                .Where(agent => agent.Key == normalizedKey)
                .OrderBy(agent => agent.Version)
                .ToListAsync(cancellationToken);

            var nextVersion = existingConfigs.Count == 0 ? 1 : existingConfigs[^1].Version + 1;
            var latestConfig = existingConfigs.LastOrDefault();

            foreach (var existingConfig in existingConfigs.Where(agent => agent.IsActive))
            {
                existingConfig.IsActive = false;
            }

            var entity = new AgentConfigEntity
            {
                Key = normalizedKey,
                Version = nextVersion,
                ConfigJson = configJson,
                CreatedAtUtc = DateTime.UtcNow,
                CreatedBy = normalizedCreatedBy,
                IsActive = true,
                OwningWorkflowKey = latestConfig?.OwningWorkflowKey,
                ForkedFromKey = latestConfig?.ForkedFromKey,
                ForkedFromVersion = latestConfig?.ForkedFromVersion,
                TagsJson = tags is null
                    ? latestConfig?.TagsJson ?? "[]"
                    : WorkflowJson.SerializeTags(TagNormalizer.Normalize(tags))
            };

            dbContext.Agents.Add(entity);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            Cache.Set(AgentConfigCacheKey.Create(normalizedKey, nextVersion), Map(entity, configuration));

            return nextVersion;
        });
    }

    public Task<int> CreateNewVersionAsync(
        string key,
        AgentInvocationConfiguration configuration,
        string? createdBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return CreateNewVersionAsync(
            key,
            AgentConfigJson.Serialize(configuration),
            createdBy,
            cancellationToken);
    }

    public async Task<AgentConfig> CreateForkAsync(
        string sourceKey,
        int sourceVersion,
        string workflowKey,
        string configJson,
        string? createdBy,
        CancellationToken cancellationToken = default)
    {
        return await CreateForkAsync(
            sourceKey,
            sourceVersion,
            workflowKey,
            configJson,
            createdBy,
            tags: null,
            cancellationToken);
    }

    public async Task<AgentConfig> CreateForkAsync(
        string sourceKey,
        int sourceVersion,
        string workflowKey,
        string configJson,
        string? createdBy,
        IReadOnlyList<string>? tags,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(configJson);

        var normalizedSourceKey = NormalizeKey(sourceKey);
        var normalizedWorkflowKey = workflowKey.Trim();
        var normalizedCreatedBy = NormalizeCreatedBy(createdBy);
        _ = AgentConfigJson.Deserialize(configJson);

        // Synthetic, server-minted key. Prefix keeps forks out of the user-facing validator's
        // legal-key space; guid body is short enough to fit in the 128-char column with room to spare.
        var forkKey = "__fork_" + Guid.NewGuid().ToString("N")[..16];

        var strategy = dbContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            dbContext.ChangeTracker.Clear();

            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);

            var source = await dbContext.Agents
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    agent => agent.Key == normalizedSourceKey && agent.Version == sourceVersion,
                    cancellationToken);

            if (source is null)
            {
                throw new AgentConfigNotFoundException(normalizedSourceKey, sourceVersion);
            }

            var entity = new AgentConfigEntity
            {
                Key = forkKey,
                Version = 1,
                ConfigJson = configJson,
                CreatedAtUtc = DateTime.UtcNow,
                CreatedBy = normalizedCreatedBy,
                IsActive = true,
                OwningWorkflowKey = normalizedWorkflowKey,
                ForkedFromKey = normalizedSourceKey,
                ForkedFromVersion = sourceVersion,
                TagsJson = tags is null
                    ? source.TagsJson
                    : WorkflowJson.SerializeTags(TagNormalizer.Normalize(tags))
            };

            dbContext.Agents.Add(entity);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            var mapped = Map(entity);
            Cache.Set(AgentConfigCacheKey.Create(forkKey, 1), mapped);
            return mapped;
        });
    }

    public async Task<int> CreatePublishedVersionAsync(
        string targetKey,
        string configJson,
        string forkedFromKey,
        int forkedFromVersion,
        string? createdBy,
        CancellationToken cancellationToken = default)
    {
        return await CreatePublishedVersionAsync(
            targetKey,
            configJson,
            forkedFromKey,
            forkedFromVersion,
            createdBy,
            tags: null,
            cancellationToken);
    }

    public async Task<int> CreatePublishedVersionAsync(
        string targetKey,
        string configJson,
        string forkedFromKey,
        int forkedFromVersion,
        string? createdBy,
        IReadOnlyList<string>? tags,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configJson);

        var normalizedTarget = NormalizeKey(targetKey);
        var normalizedLineageKey = NormalizeKey(forkedFromKey);
        var normalizedCreatedBy = NormalizeCreatedBy(createdBy);
        _ = AgentConfigJson.Deserialize(configJson);

        var strategy = dbContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            dbContext.ChangeTracker.Clear();

            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);

            var existingConfigs = await dbContext.Agents
                .Where(agent => agent.Key == normalizedTarget)
                .OrderBy(agent => agent.Version)
                .ToListAsync(cancellationToken);

            var nextVersion = existingConfigs.Count == 0 ? 1 : existingConfigs[^1].Version + 1;
            var latestConfig = existingConfigs.LastOrDefault();

            foreach (var existingConfig in existingConfigs.Where(agent => agent.IsActive))
            {
                existingConfig.IsActive = false;
            }

            var entity = new AgentConfigEntity
            {
                Key = normalizedTarget,
                Version = nextVersion,
                ConfigJson = configJson,
                CreatedAtUtc = DateTime.UtcNow,
                CreatedBy = normalizedCreatedBy,
                IsActive = true,
                ForkedFromKey = normalizedLineageKey,
                ForkedFromVersion = forkedFromVersion,
                TagsJson = tags is null
                    ? latestConfig?.TagsJson ?? "[]"
                    : WorkflowJson.SerializeTags(TagNormalizer.Normalize(tags))
                // OwningWorkflowKey intentionally null — published agents are library-wide.
            };

            dbContext.Agents.Add(entity);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            Cache.Set(AgentConfigCacheKey.Create(normalizedTarget, nextVersion), Map(entity));
            return nextVersion;
        });
    }

    public async Task<bool> RetireAsync(string key, CancellationToken cancellationToken = default)
    {
        var normalizedKey = NormalizeKey(key);

        var entities = await dbContext.Agents
            .Where(agent => agent.Key == normalizedKey)
            .ToListAsync(cancellationToken);

        if (entities.Count == 0)
        {
            return false;
        }

        var changed = false;
        foreach (var entity in entities)
        {
            if (!entity.IsRetired)
            {
                entity.IsRetired = true;
                changed = true;
            }
        }

        if (changed)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return true;
    }

    public async Task<IReadOnlyList<string>> RetireManyAsync(
        IReadOnlyList<string> keys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);

        var normalizedKeys = keys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(NormalizeKey)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (normalizedKeys.Length == 0)
        {
            return Array.Empty<string>();
        }

        var entities = await dbContext.Agents
            .Where(agent => normalizedKeys.Contains(agent.Key))
            .ToListAsync(cancellationToken);

        if (entities.Count == 0)
        {
            return Array.Empty<string>();
        }

        var changed = false;
        foreach (var entity in entities)
        {
            if (!entity.IsRetired)
            {
                entity.IsRetired = true;
                changed = true;
            }
        }

        if (changed)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return entities
            .Select(entity => entity.Key)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<int> GetLatestVersionAsync(string key, CancellationToken cancellationToken = default)
    {
        var normalizedKey = NormalizeKey(key);

        var latest = await dbContext.Agents
            .AsNoTracking()
            .Where(agent => agent.Key == normalizedKey)
            .OrderByDescending(agent => agent.Version)
            .Select(agent => (int?)agent.Version)
            .FirstOrDefaultAsync(cancellationToken);

        if (latest is null)
        {
            throw new AgentConfigNotFoundException(normalizedKey, version: 0);
        }

        return latest.Value;
    }

    private static AgentConfig Map(AgentConfigEntity entity)
    {
        return Map(entity, AgentConfigJson.Deserialize(entity.ConfigJson));
    }

    private static AgentConfig Map(AgentConfigEntity entity, AgentInvocationConfiguration configuration)
    {
        return new AgentConfig(
            entity.Key,
            entity.Version,
            AgentConfigJson.ReadKind(entity.ConfigJson),
            configuration,
            entity.ConfigJson,
            DateTime.SpecifyKind(entity.CreatedAtUtc, DateTimeKind.Utc),
            entity.CreatedBy,
            AgentConfigJson.ReadOutputs(entity.ConfigJson),
            entity.OwningWorkflowKey,
            entity.ForkedFromKey,
            entity.ForkedFromVersion,
            TagNormalizer.Normalize(WorkflowJson.DeserializeTags(entity.TagsJson)));
    }

    private static string NormalizeKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        return key.Trim();
    }

    private static string? NormalizeCreatedBy(string? createdBy)
    {
        return string.IsNullOrWhiteSpace(createdBy) ? null : createdBy.Trim();
    }
}
