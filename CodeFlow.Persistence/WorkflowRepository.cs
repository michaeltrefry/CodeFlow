using CodeFlow.Runtime;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using System.Data;
using System.Text.Json;

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

        var entity = await dbContext.Workflows
            .AsNoTracking()
            .Include(workflow => workflow.Edges)
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

        var entity = await dbContext.Workflows
            .AsNoTracking()
            .Include(workflow => workflow.Edges)
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
        var entities = await dbContext.Workflows
            .AsNoTracking()
            .Include(workflow => workflow.Edges)
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
        var entities = await dbContext.Workflows
            .AsNoTracking()
            .Include(workflow => workflow.Edges)
            .Where(workflow => workflow.Key == normalizedKey)
            .OrderByDescending(workflow => workflow.Version)
            .ToListAsync(cancellationToken);

        return entities.Select(Map).ToArray();
    }

    public async Task<WorkflowEdge?> FindNextAsync(
        string key,
        int version,
        string fromAgentKey,
        AgentDecision decision,
        JsonElement? discriminator = null,
        CancellationToken cancellationToken = default)
    {
        var workflow = await GetAsync(key, version, cancellationToken);
        return workflow.FindNext(fromAgentKey, decision, discriminator);
    }

    public async Task<int> CreateNewVersionAsync(
        WorkflowDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var normalizedKey = NormalizeKey(draft.Key);

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
            StartAgentKey = draft.StartAgentKey.Trim(),
            EscalationAgentKey = string.IsNullOrWhiteSpace(draft.EscalationAgentKey) ? null : draft.EscalationAgentKey.Trim(),
            MaxRoundsPerRound = draft.MaxRoundsPerRound,
            CreatedAtUtc = DateTime.UtcNow,
            Edges = draft.Edges
                .Select((edge, index) => new WorkflowEdgeEntity
                {
                    FromAgentKey = edge.FromAgentKey.Trim(),
                    Decision = edge.Decision,
                    DiscriminatorJson = edge.Discriminator is null ? null : JsonSerializer.Serialize(edge.Discriminator),
                    ToAgentKey = edge.ToAgentKey.Trim(),
                    RotatesRound = edge.RotatesRound,
                    SortOrder = edge.SortOrder == 0 ? index : edge.SortOrder
                })
                .ToList()
        };

        dbContext.Workflows.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var mapped = Map(entity);
        Cache[WorkflowCacheKey.Create(normalizedKey, nextVersion)] = mapped;

        return nextVersion;
    }

    private static Workflow Map(WorkflowEntity entity)
    {
        return new Workflow(
            entity.Key,
            entity.Version,
            entity.Name,
            entity.StartAgentKey,
            entity.EscalationAgentKey,
            entity.MaxRoundsPerRound,
            DateTime.SpecifyKind(entity.CreatedAtUtc, DateTimeKind.Utc),
            entity.Edges
                .OrderBy(edge => edge.SortOrder)
                .Select(Map)
                .ToArray());
    }

    private static WorkflowEdge Map(WorkflowEdgeEntity entity)
    {
        return new WorkflowEdge(
            entity.FromAgentKey,
            entity.Decision,
            WorkflowJson.DeserializeDiscriminator(entity.DiscriminatorJson),
            entity.ToAgentKey,
            entity.RotatesRound,
            entity.SortOrder);
    }

    private static string NormalizeKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        return key.Trim();
    }
}
