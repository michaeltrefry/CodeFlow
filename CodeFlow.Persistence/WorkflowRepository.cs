using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System.Data;

namespace CodeFlow.Persistence;

public sealed class WorkflowRepository(CodeFlowDbContext dbContext) : IWorkflowRepository
{
    // Process-wide cache for immutable workflow versions. Bounded by size (up to ~512 entries —
    // workflow objects are small) and sliding expiration so long-lived processes don't retain
    // every version they ever touched.
    private const int CacheSizeLimit = 512;
    private static readonly TimeSpan CacheSlidingExpiration = TimeSpan.FromMinutes(15);
    private static readonly MemoryCache Cache = new(new MemoryCacheOptions
    {
        SizeLimit = CacheSizeLimit,
    });
    private static readonly MemoryCacheEntryOptions CacheEntryOptions = new MemoryCacheEntryOptions()
        .SetSize(1)
        .SetSlidingExpiration(CacheSlidingExpiration);

    public async Task<Workflow> GetAsync(
        string key,
        int version,
        CancellationToken cancellationToken = default)
    {
        var normalizedKey = NormalizeKey(key);
        var cacheKey = WorkflowCacheKey.Create(normalizedKey, version);

        if (Cache.TryGetValue<Workflow>(cacheKey, out var cachedWorkflow) && cachedWorkflow is not null)
        {
            return cachedWorkflow;
        }

        var entity = await LoadWorkflowsQuery()
            .SingleOrDefaultAsync(
                workflow => workflow.Key == normalizedKey && workflow.Version == version,
                cancellationToken);

        if (entity is null)
        {
            throw new WorkflowNotFoundException(normalizedKey, version);
        }

        var mapped = Map(entity);
        Cache.Set(cacheKey, mapped, CacheEntryOptions);
        return mapped;
    }

    public async Task<Workflow?> GetLatestAsync(string key, CancellationToken cancellationToken = default)
    {
        var normalizedKey = NormalizeKey(key);

        var entity = await LoadWorkflowsQuery()
            .Where(workflow => workflow.Key == normalizedKey)
            .OrderByDescending(workflow => workflow.Version)
            .FirstOrDefaultAsync(cancellationToken);

        if (entity is null)
        {
            return null;
        }

        var mapped = Map(entity);
        Cache.Set(WorkflowCacheKey.Create(normalizedKey, entity.Version), mapped, CacheEntryOptions);
        return mapped;
    }

    public async Task<IReadOnlyList<Workflow>> ListLatestAsync(CancellationToken cancellationToken = default)
    {
        var entities = await LoadWorkflowsQuery()
            .GroupBy(workflow => workflow.Key)
            .Select(group => group
                .OrderByDescending(workflow => workflow.Version)
                .First())
            .ToListAsync(cancellationToken);

        return entities
            .OrderBy(entity => entity.Key)
            .Select(Map)
            .ToArray();
    }

    public async Task<IReadOnlyList<Workflow>> ListVersionsAsync(string key, CancellationToken cancellationToken = default)
    {
        var normalizedKey = NormalizeKey(key);
        var entities = await LoadWorkflowsQuery()
            .Where(workflow => workflow.Key == normalizedKey)
            .OrderByDescending(workflow => workflow.Version)
            .ToListAsync(cancellationToken);

        return entities.Select(Map).ToArray();
    }

    public async Task<WorkflowEdge?> FindNextAsync(
        string key,
        int version,
        Guid fromNodeId,
        string outputPortName,
        CancellationToken cancellationToken = default)
    {
        var workflow = await GetAsync(key, version, cancellationToken);
        return workflow.FindNext(fromNodeId, outputPortName);
    }

    public async Task<int> CreateNewVersionAsync(
        WorkflowDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var normalizedKey = NormalizeKey(draft.Key);

        var strategy = dbContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            dbContext.ChangeTracker.Clear();

            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);

            var existing = await dbContext.Workflows
                .Where(workflow => workflow.Key == normalizedKey)
                .OrderByDescending(workflow => workflow.Version)
                .Select(workflow => workflow.Version)
                .FirstOrDefaultAsync(cancellationToken);

            var nextVersion = existing == 0 ? 1 : existing + 1;

            var entity = new WorkflowEntity
            {
                Key = normalizedKey,
                Version = nextVersion,
                Name = draft.Name.Trim(),
                MaxRoundsPerRound = draft.MaxRoundsPerRound,
                Category = draft.Category,
                TagsJson = WorkflowJson.SerializeTags(NormalizeTags(draft.Tags)),
                CreatedAtUtc = DateTime.UtcNow,
                Nodes = draft.Nodes
                    .Select(node => new WorkflowNodeEntity
                    {
                        NodeId = node.Id,
                        Kind = node.Kind,
                        AgentKey = NormalizeOptionalString(node.AgentKey),
                        AgentVersion = node.AgentVersion,
                        OutputScript = node.OutputScript,
                        InputScript = node.InputScript,
                        OutputPortsJson = WorkflowJson.SerializePorts(node.OutputPorts),
                        LayoutX = node.LayoutX,
                        LayoutY = node.LayoutY,
                        SubflowKey = NormalizeOptionalString(node.SubflowKey),
                        SubflowVersion = node.SubflowVersion,
                        ReviewMaxRounds = node.ReviewMaxRounds,
                        LoopDecision = NormalizeOptionalString(node.LoopDecision)
                    })
                    .ToList(),
                Edges = draft.Edges
                    .Select((edge, index) => new WorkflowEdgeEntity
                    {
                        FromNodeId = edge.FromNodeId,
                        FromPort = edge.FromPort,
                        ToNodeId = edge.ToNodeId,
                        ToPort = string.IsNullOrWhiteSpace(edge.ToPort) ? WorkflowEdge.DefaultInputPort : edge.ToPort,
                        RotatesRound = edge.RotatesRound,
                        SortOrder = edge.SortOrder == 0 ? index : edge.SortOrder
                    })
                    .ToList(),
                Inputs = draft.Inputs
                    .Select(input => new WorkflowInputEntity
                    {
                        Key = input.Key.Trim(),
                        DisplayName = input.DisplayName.Trim(),
                        Kind = input.Kind,
                        Required = input.Required,
                        DefaultValueJson = input.DefaultValueJson,
                        Description = input.Description,
                        Ordinal = input.Ordinal
                    })
                    .ToList()
            };

            dbContext.Workflows.Add(entity);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            var mapped = Map(entity);
            Cache.Set(WorkflowCacheKey.Create(normalizedKey, nextVersion), mapped, CacheEntryOptions);

            return nextVersion;
        });
    }

    private IQueryable<WorkflowEntity> LoadWorkflowsQuery()
    {
        return dbContext.Workflows
            .AsNoTracking()
            .AsSplitQuery()
            .Include(workflow => workflow.Nodes)
            .Include(workflow => workflow.Edges)
            .Include(workflow => workflow.Inputs);
    }

    private static Workflow Map(WorkflowEntity entity)
    {
        return new Workflow(
            entity.Key,
            entity.Version,
            entity.Name,
            entity.MaxRoundsPerRound,
            DateTime.SpecifyKind(entity.CreatedAtUtc, DateTimeKind.Utc),
            entity.Nodes
                .Select(Map)
                .ToArray(),
            entity.Edges
                .OrderBy(edge => edge.SortOrder)
                .Select(Map)
                .ToArray(),
            entity.Inputs
                .OrderBy(input => input.Ordinal)
                .Select(Map)
                .ToArray(),
            entity.Category,
            WorkflowJson.DeserializeTags(entity.TagsJson));
    }

    private static IReadOnlyList<string> NormalizeTags(IReadOnlyList<string>? tags)
    {
        if (tags is null || tags.Count == 0)
        {
            return Array.Empty<string>();
        }

        return tags
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToArray();
    }

    private static WorkflowNode Map(WorkflowNodeEntity entity)
    {
        return new WorkflowNode(
            entity.NodeId,
            entity.Kind,
            entity.AgentKey,
            entity.AgentVersion,
            entity.OutputScript,
            WorkflowJson.DeserializePorts(entity.OutputPortsJson),
            entity.LayoutX,
            entity.LayoutY,
            entity.SubflowKey,
            entity.SubflowVersion,
            entity.ReviewMaxRounds,
            entity.LoopDecision,
            entity.InputScript);
    }

    private static WorkflowEdge Map(WorkflowEdgeEntity entity)
    {
        return new WorkflowEdge(
            entity.FromNodeId,
            entity.FromPort,
            entity.ToNodeId,
            string.IsNullOrWhiteSpace(entity.ToPort) ? WorkflowEdge.DefaultInputPort : entity.ToPort,
            entity.RotatesRound,
            entity.SortOrder);
    }

    private static WorkflowInput Map(WorkflowInputEntity entity)
    {
        return new WorkflowInput(
            entity.Key,
            entity.DisplayName,
            entity.Kind,
            entity.Required,
            entity.DefaultValueJson,
            entity.Description,
            entity.Ordinal);
    }

    private static string NormalizeKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        return key.Trim();
    }

    private static string? NormalizeOptionalString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }
}
