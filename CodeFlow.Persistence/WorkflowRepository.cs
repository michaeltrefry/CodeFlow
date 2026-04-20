using CodeFlow.Runtime;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
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
