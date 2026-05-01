using CodeFlow.Persistence;
using CodeFlow.Runtime.Workspace;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeFlow.Host.Cleanup;

public sealed class CodeFlowCleanupRunner
{
    private static readonly string[] TerminalTraceStates = ["Completed", "Failed"];

    private readonly CodeFlowDbContext dbContext;
    private readonly IOptions<WorkspaceOptions> workspaceOptions;
    private readonly ILogger<CodeFlowCleanupRunner> logger;
    private readonly ILoggerFactory loggerFactory;
    private readonly TimeProvider timeProvider;

    public CodeFlowCleanupRunner(
        CodeFlowDbContext dbContext,
        IOptions<WorkspaceOptions> workspaceOptions,
        ILogger<CodeFlowCleanupRunner> logger,
        ILoggerFactory loggerFactory,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(workspaceOptions);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        this.dbContext = dbContext;
        this.workspaceOptions = workspaceOptions;
        this.logger = logger;
        this.loggerFactory = loggerFactory;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<TraceCleanupResult> DeleteOldTerminalTracesAsync(
        int olderThanDays,
        CancellationToken cancellationToken = default)
    {
        if (olderThanDays < 1 || olderThanDays > 3650)
        {
            throw new ArgumentOutOfRangeException(
                nameof(olderThanDays),
                olderThanDays,
                "Trace retention days must be between 1 and 3650.");
        }

        var cutoffUtc = timeProvider.GetUtcNow().UtcDateTime.AddDays(-olderThanDays);
        var sagas = await dbContext.WorkflowSagas
            .Where(saga => TerminalTraceStates.Contains(saga.CurrentState)
                && saga.UpdatedAtUtc <= cutoffUtc)
            .ToListAsync(cancellationToken);

        return await DeleteTracesAsync(sagas, cancellationToken);
    }

    public async Task<RetiredObjectCleanupResult> DeleteUnreferencedRetiredObjectsAsync(
        CancellationToken cancellationToken = default)
    {
        var workflowsDeleted = await DeleteUnreferencedRetiredWorkflowsAsync(cancellationToken);
        var agentsDeleted = await DeleteUnreferencedRetiredAgentsAsync(cancellationToken);
        var rolesDeleted = await DeleteUnreferencedRetiredRolesAsync(cancellationToken);

        return new RetiredObjectCleanupResult(workflowsDeleted, agentsDeleted, rolesDeleted);
    }

    private async Task<TraceCleanupResult> DeleteTracesAsync(
        IReadOnlyCollection<WorkflowSagaStateEntity> sagas,
        CancellationToken cancellationToken)
    {
        if (sagas.Count == 0)
        {
            return new TraceCleanupResult(0, 0);
        }

        var traceIds = sagas.Select(saga => saga.TraceId).ToArray();
        var correlationIds = sagas.Select(saga => saga.CorrelationId).ToArray();

        await RemoveRangeAsync(
            dbContext.HitlTasks.Where(task => traceIds.Contains(task.TraceId)),
            cancellationToken);
        await RemoveRangeAsync(
            dbContext.WorkflowSagaDecisions.Where(decision => correlationIds.Contains(decision.SagaCorrelationId)),
            cancellationToken);
        await RemoveRangeAsync(
            dbContext.WorkflowSagaLogicEvaluations.Where(evaluation => correlationIds.Contains(evaluation.SagaCorrelationId)),
            cancellationToken);
        await RemoveRangeAsync(
            dbContext.TokenUsageRecords.Where(record => traceIds.Contains(record.TraceId)),
            cancellationToken);
        await RemoveRangeAsync(
            dbContext.RefusalEvents.Where(@event => @event.TraceId != null && traceIds.Contains(@event.TraceId.Value)),
            cancellationToken);
        await RemoveRangeAsync(
            dbContext.AgentInvocationAuthority.Where(snapshot => traceIds.Contains(snapshot.TraceId)),
            cancellationToken);
        await RemoveRangeAsync(
            dbContext.ReplayAttempts.Where(attempt => traceIds.Contains(attempt.ParentTraceId)),
            cancellationToken);

        dbContext.WorkflowSagas.RemoveRange(sagas);
        await dbContext.SaveChangesAsync(cancellationToken);

        var workdirsDeleted = TryRemoveTraceWorkdirs(sagas);
        logger.LogInformation(
            "Trace retention cleanup deleted {TraceCount} traces and {WorkdirCount} workdirs.",
            sagas.Count,
            workdirsDeleted);

        return new TraceCleanupResult(sagas.Count, workdirsDeleted);
    }

    private async Task<int> DeleteUnreferencedRetiredWorkflowsAsync(CancellationToken cancellationToken)
    {
        var workflows = await dbContext.Workflows
            .AsNoTracking()
            .Include(workflow => workflow.Nodes)
            .ToListAsync(cancellationToken);

        var activeWorkflows = workflows
            .Where(workflow => !workflow.IsRetired)
            .ToArray();

        var nodesByWorkflowKey = workflows
            .GroupBy(workflow => workflow.Key, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.SelectMany(workflow => workflow.Nodes).ToArray(),
                StringComparer.Ordinal);

        var protectedKeys = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>(activeWorkflows
            .SelectMany(workflow => workflow.Nodes)
            .Select(node => node.SubflowKey)
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .Select(static key => key!));

        while (queue.TryDequeue(out var key))
        {
            if (!protectedKeys.Add(key))
            {
                continue;
            }

            if (!nodesByWorkflowKey.TryGetValue(key, out var nodes))
            {
                continue;
            }

            foreach (var childKey in nodes
                .Select(node => node.SubflowKey)
                .Where(static childKey => !string.IsNullOrWhiteSpace(childKey)))
            {
                queue.Enqueue(childKey!);
            }
        }

        var deleteKeys = workflows
            .Where(workflow => workflow.IsRetired && !protectedKeys.Contains(workflow.Key))
            .Select(workflow => workflow.Key)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (deleteKeys.Length == 0)
        {
            return 0;
        }

        var toDelete = await dbContext.Workflows
            .Where(workflow => deleteKeys.Contains(workflow.Key))
            .ToListAsync(cancellationToken);

        dbContext.Workflows.RemoveRange(toDelete);
        await dbContext.SaveChangesAsync(cancellationToken);
        WorkflowRepository.ClearCache();

        logger.LogInformation(
            "Retired-object cleanup deleted {WorkflowCount} workflow versions across {WorkflowKeyCount} workflow keys.",
            toDelete.Count,
            deleteKeys.Length);

        return toDelete.Count;
    }

    private async Task<int> DeleteUnreferencedRetiredAgentsAsync(CancellationToken cancellationToken)
    {
        var referencedAgentKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in await dbContext.WorkflowNodes
            .AsNoTracking()
            .Where(node => node.AgentKey != null)
            .Select(node => node.AgentKey!)
            .Distinct()
            .ToListAsync(cancellationToken))
        {
            referencedAgentKeys.Add(key);
        }
        foreach (var key in await dbContext.WorkflowNodes
            .AsNoTracking()
            .Where(node => node.ContributorAgentKey != null)
            .Select(node => node.ContributorAgentKey!)
            .Distinct()
            .ToListAsync(cancellationToken))
        {
            referencedAgentKeys.Add(key);
        }
        foreach (var key in await dbContext.WorkflowNodes
            .AsNoTracking()
            .Where(node => node.SynthesizerAgentKey != null)
            .Select(node => node.SynthesizerAgentKey!)
            .Distinct()
            .ToListAsync(cancellationToken))
        {
            referencedAgentKeys.Add(key);
        }
        foreach (var key in await dbContext.WorkflowNodes
            .AsNoTracking()
            .Where(node => node.CoordinatorAgentKey != null)
            .Select(node => node.CoordinatorAgentKey!)
            .Distinct()
            .ToListAsync(cancellationToken))
        {
            referencedAgentKeys.Add(key);
        }

        var retiredAgentKeys = await dbContext.Agents
            .AsNoTracking()
            .Where(agent => agent.IsRetired)
            .Select(agent => agent.Key)
            .Distinct()
            .ToListAsync(cancellationToken);

        var deleteKeys = retiredAgentKeys
            .Where(key => !referencedAgentKeys.Contains(key))
            .ToArray();

        if (deleteKeys.Length == 0)
        {
            return 0;
        }

        await RemoveRangeAsync(
            dbContext.AgentRoleAssignments.Where(assignment => deleteKeys.Contains(assignment.AgentKey)),
            cancellationToken);

        var toDelete = await dbContext.Agents
            .Where(agent => deleteKeys.Contains(agent.Key))
            .ToListAsync(cancellationToken);

        dbContext.Agents.RemoveRange(toDelete);
        await dbContext.SaveChangesAsync(cancellationToken);
        AgentConfigRepository.ClearCache();

        logger.LogInformation(
            "Retired-object cleanup deleted {AgentCount} agent versions across {AgentKeyCount} agent keys.",
            toDelete.Count,
            deleteKeys.Length);

        return toDelete.Count;
    }

    private async Task<int> DeleteUnreferencedRetiredRolesAsync(CancellationToken cancellationToken)
    {
        var referencedRoleIds = await (
                from assignment in dbContext.AgentRoleAssignments.AsNoTracking()
                join agent in dbContext.Agents.AsNoTracking()
                    on assignment.AgentKey equals agent.Key
                select assignment.RoleId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var referencedRoleIdArray = referencedRoleIds.ToArray();
        var rolesToDelete = await dbContext.AgentRoles
            .Where(role => role.IsRetired
                && !role.IsSystemManaged
                && !referencedRoleIdArray.Contains(role.Id))
            .ToListAsync(cancellationToken);

        if (rolesToDelete.Count == 0)
        {
            return 0;
        }

        dbContext.AgentRoles.RemoveRange(rolesToDelete);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Retired-object cleanup deleted {RoleCount} retired roles.",
            rolesToDelete.Count);

        return rolesToDelete.Count;
    }

    private int TryRemoveTraceWorkdirs(IReadOnlyCollection<WorkflowSagaStateEntity> sagas)
    {
        var loggerForCleanup = loggerFactory.CreateLogger(typeof(CodeFlowCleanupRunner));
        var topLevelSagas = sagas.Where(static saga => saga.ParentTraceId is null);
        var deleted = 0;

        foreach (var saga in topLevelSagas)
        {
            if (TraceWorkdirCleanup.TryRemove(
                    workspaceOptions.Value.WorkingDirectoryRoot,
                    saga.TraceId,
                    loggerForCleanup))
            {
                deleted++;
            }
        }

        return deleted;
    }

    private async Task RemoveRangeAsync<TEntity>(
        IQueryable<TEntity> query,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        var entities = await query.ToListAsync(cancellationToken);
        if (entities.Count > 0)
        {
            dbContext.RemoveRange(entities);
        }
    }
}

public sealed record TraceCleanupResult(int DeletedTraces, int DeletedWorkdirs);

public sealed record RetiredObjectCleanupResult(
    int DeletedWorkflows,
    int DeletedAgents,
    int DeletedRoles);
