using CodeFlow.Runtime;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using System.Data;

namespace CodeFlow.Persistence;

public sealed class AgentConfigRepository(CodeFlowDbContext dbContext) : IAgentConfigRepository
{
    private static readonly ConcurrentDictionary<AgentConfigCacheKey, AgentConfig> Cache = new();

    public async Task<AgentConfig> GetAsync(
        string key,
        int version,
        CancellationToken cancellationToken = default)
    {
        var normalizedKey = NormalizeKey(key);
        var cacheKey = AgentConfigCacheKey.Create(normalizedKey, version);

        if (Cache.TryGetValue(cacheKey, out var cachedConfig))
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
            throw new AgentConfigNotFoundException(normalizedKey, version);
        }

        return Cache.GetOrAdd(cacheKey, _ => Map(entity));
    }

    public async Task<int> CreateNewVersionAsync(
        string key,
        string configJson,
        string? createdBy,
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
                IsActive = true
            };

            dbContext.Agents.Add(entity);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            Cache[AgentConfigCacheKey.Create(normalizedKey, nextVersion)] = Map(entity, configuration);

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
            AgentConfigJson.ReadOutputs(entity.ConfigJson));
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
