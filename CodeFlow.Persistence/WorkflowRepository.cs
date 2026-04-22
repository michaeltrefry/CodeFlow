using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using System.Data;

namespace CodeFlow.Persistence;

public sealed class WorkflowRepository(CodeFlowDbContext dbContext) : IWorkflowRepository
{
    private static readonly ConcurrentDictionary<WorkflowCacheKey, Workflow> Cache = new();

    public async Task<Workflow> GetAsync(
        string key,
        int version,
        CancellationToken cancellationToken = default)
    {
        var normalizedKey = NormalizeKey(key);
        var cacheKey = WorkflowCacheKey.Create(normalizedKey, version);

        if (Cache.TryGetValue(cacheKey, out var cachedWorkflow))
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

        return Cache.GetOrAdd(cacheKey, _ => Map(entity));
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

        return Cache.GetOrAdd(WorkflowCacheKey.Create(normalizedKey, entity.Version), _ => Map(entity));
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
                CreatedAtUtc = DateTime.UtcNow,
                Nodes = draft.Nodes
                    .Select(node => new WorkflowNodeEntity
                    {
                        NodeId = node.Id,
                        Kind = node.Kind,
                        AgentKey = NormalizeOptionalString(node.AgentKey),
                        AgentVersion = node.AgentVersion,
                        Script = node.Script,
                        OutputPortsJson = WorkflowJson.SerializePorts(node.OutputPorts),
                        LayoutX = node.LayoutX,
                        LayoutY = node.LayoutY
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
            Cache[WorkflowCacheKey.Create(normalizedKey, nextVersion)] = mapped;

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
                .ToArray());
    }

    private static WorkflowNode Map(WorkflowNodeEntity entity)
    {
        return new WorkflowNode(
            entity.NodeId,
            entity.Kind,
            entity.AgentKey,
            entity.AgentVersion,
            entity.Script,
            WorkflowJson.DeserializePorts(entity.OutputPortsJson),
            entity.LayoutX,
            entity.LayoutY);
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
