using CodeFlow.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CodeFlow.Api.CascadeBump;

/// <summary>
/// E4: builds a cascade-bump plan against the current DB state. The plan walks the
/// reverse-pin graph in BFS order from the bump root: every workflow whose **latest version**
/// pins the source at <c>FromVersion</c> gets a step, and the cascade continues with each
/// bumped workflow as a new source. The BFS discovery order also serves as the apply order
/// (deepest first, ancestors last) — once <c>A</c> has been planned to v<i>n</i>, every later
/// step pinning <c>A</c> can rewrite to that new version.
/// </summary>
public sealed class CascadeBumpPlanner(CodeFlowDbContext dbContext)
{
    public async Task<CascadeBumpPlan> PlanAsync(
        CascadeBumpRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var rootKey = NormalizeKey(request.Key, nameof(request.Key));

        if (request.FromVersion <= 0 || request.ToVersion <= 0)
        {
            throw new ArgumentException(
                $"Cascade-bump versions must be positive (got from={request.FromVersion}, to={request.ToVersion}).",
                nameof(request));
        }

        if (request.FromVersion >= request.ToVersion)
        {
            throw new ArgumentException(
                $"Cascade-bump fromVersion ({request.FromVersion}) must be strictly less than toVersion ({request.ToVersion}).",
                nameof(request));
        }

        await EnsureRootExistsAsync(request.RootKind, rootKey, request.ToVersion, cancellationToken);

        var excluded = NormalizeExcludes(request.ExcludeWorkflows);
        var root = new CascadeBumpRoot(request.RootKind, rootKey, request.FromVersion, request.ToVersion);

        var stepsByKey = new Dictionary<string, MutableStep>(StringComparer.Ordinal);
        var orderedSteps = new List<MutableStep>();
        var findings = new List<CascadeBumpFinding>();
        var queue = new Queue<PendingSource>();
        var enqueuedKeys = new HashSet<string>(StringComparer.Ordinal);

        queue.Enqueue(new PendingSource(
            ReferenceKind: request.RootKind == CascadeBumpRootKind.Agent
                ? CascadeBumpReferenceKind.Agent
                : CascadeBumpReferenceKind.Subflow,
            Key: rootKey,
            FromVersion: request.FromVersion,
            ToVersion: request.ToVersion));

        while (queue.Count > 0)
        {
            var source = queue.Dequeue();
            var pinningNodes = await FindLatestPinnersAsync(source, cancellationToken);

            foreach (var pinningNode in pinningNodes)
            {
                if (excluded.Contains(pinningNode.WorkflowKey))
                {
                    findings.Add(new CascadeBumpFinding(
                        Severity: "Info",
                        Code: "WorkflowExcluded",
                        Message: $"Workflow '{pinningNode.WorkflowKey}' was excluded from the cascade. "
                            + $"Downstream workflows that pin '{pinningNode.WorkflowKey}' will not be auto-bumped."));
                    continue;
                }

                var pinChange = new CascadeBumpPinChange(
                    NodeId: pinningNode.NodeId,
                    ReferenceKind: source.ReferenceKind,
                    Key: source.Key,
                    FromVersion: source.FromVersion,
                    ToVersion: source.ToVersion);

                if (stepsByKey.TryGetValue(pinningNode.WorkflowKey, out var existing))
                {
                    existing.PinChanges.Add(pinChange);
                    continue;
                }

                var step = new MutableStep(
                    WorkflowKey: pinningNode.WorkflowKey,
                    FromVersion: pinningNode.WorkflowVersion,
                    ToVersion: pinningNode.WorkflowVersion + 1,
                    PinChanges: new List<CascadeBumpPinChange> { pinChange });

                stepsByKey[pinningNode.WorkflowKey] = step;
                orderedSteps.Add(step);

                if (enqueuedKeys.Add(pinningNode.WorkflowKey))
                {
                    queue.Enqueue(new PendingSource(
                        ReferenceKind: CascadeBumpReferenceKind.Subflow,
                        Key: pinningNode.WorkflowKey,
                        FromVersion: pinningNode.WorkflowVersion,
                        ToVersion: pinningNode.WorkflowVersion + 1));
                }
            }
        }

        if (orderedSteps.Count == 0)
        {
            findings.Add(new CascadeBumpFinding(
                Severity: "Info",
                Code: "NoPinners",
                Message: $"No workflows pin {DescribeRoot(root)} at v{request.FromVersion} on their latest version. "
                    + "Nothing to cascade."));
        }

        var steps = orderedSteps
            .Select(step => new CascadeBumpStep(
                WorkflowKey: step.WorkflowKey,
                FromVersion: step.FromVersion,
                ToVersion: step.ToVersion,
                PinChanges: step.PinChanges
                    .OrderBy(change => change.NodeId)
                    .ToArray()))
            .ToArray();

        return new CascadeBumpPlan(root, steps, findings);
    }

    private async Task EnsureRootExistsAsync(
        CascadeBumpRootKind kind,
        string key,
        int toVersion,
        CancellationToken cancellationToken)
    {
        bool exists = kind switch
        {
            CascadeBumpRootKind.Agent => await dbContext.Agents
                .AsNoTracking()
                .AnyAsync(a => a.Key == key && a.Version == toVersion, cancellationToken),
            CascadeBumpRootKind.Workflow => await dbContext.Workflows
                .AsNoTracking()
                .AnyAsync(w => w.Key == key && w.Version == toVersion, cancellationToken),
            _ => false,
        };

        if (!exists)
        {
            var label = kind == CascadeBumpRootKind.Agent ? "agent" : "workflow";
            throw new CascadeBumpRootNotFoundException(
                $"Cascade-bump root not found: {label} '{key}' v{toVersion} does not exist. "
                    + "Create the new version before planning a cascade.");
        }
    }

    private async Task<IReadOnlyList<PinnerHit>> FindLatestPinnersAsync(
        PendingSource source,
        CancellationToken cancellationToken)
    {
        // A workflow is a "latest pinner" iff (1) it has a node pinning the source at the
        // exact (key, fromVersion), and (2) the workflow row holding that node is itself the
        // latest version of its key. We compute (2) with a window-style subquery.
        var latestVersionByKey = dbContext.Workflows
            .GroupBy(w => w.Key)
            .Select(g => new { Key = g.Key, Latest = g.Max(w => w.Version) });

        IQueryable<PinnerHit> query;
        if (source.ReferenceKind == CascadeBumpReferenceKind.Agent)
        {
            query = from node in dbContext.WorkflowNodes.AsNoTracking()
                    join workflow in dbContext.Workflows.AsNoTracking() on node.WorkflowId equals workflow.Id
                    join latest in latestVersionByKey on workflow.Key equals latest.Key
                    where node.AgentKey == source.Key
                        && node.AgentVersion == source.FromVersion
                        && workflow.Version == latest.Latest
                    select new PinnerHit(workflow.Key, workflow.Version, node.NodeId);
        }
        else
        {
            query = from node in dbContext.WorkflowNodes.AsNoTracking()
                    join workflow in dbContext.Workflows.AsNoTracking() on node.WorkflowId equals workflow.Id
                    join latest in latestVersionByKey on workflow.Key equals latest.Key
                    where node.SubflowKey == source.Key
                        && node.SubflowVersion == source.FromVersion
                        && workflow.Version == latest.Latest
                    select new PinnerHit(workflow.Key, workflow.Version, node.NodeId);
        }

        // EF can't translate a custom comparer in OrderBy; the DB sort is good enough as a
        // tie-breaker. We re-sort client-side with an explicit Ordinal comparer so the BFS
        // discovery order is stable across providers.
        var hits = await query.ToArrayAsync(cancellationToken);
        return hits
            .OrderBy(hit => hit.WorkflowKey, StringComparer.Ordinal)
            .ThenBy(hit => hit.NodeId)
            .ToArray();
    }

    private static HashSet<string> NormalizeExcludes(IReadOnlyList<string>? excludes)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (excludes is null)
        {
            return set;
        }

        foreach (var raw in excludes)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }
            set.Add(raw.Trim());
        }
        return set;
    }

    private static string NormalizeKey(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Cascade-bump key must be non-empty.", paramName);
        }
        return value.Trim();
    }

    private static string DescribeRoot(CascadeBumpRoot root)
    {
        var label = root.Kind == CascadeBumpRootKind.Agent ? "agent" : "workflow";
        return $"{label} '{root.Key}'";
    }

    private sealed record PendingSource(
        CascadeBumpReferenceKind ReferenceKind,
        string Key,
        int FromVersion,
        int ToVersion);

    private sealed record PinnerHit(string WorkflowKey, int WorkflowVersion, Guid NodeId);

    private sealed record MutableStep(
        string WorkflowKey,
        int FromVersion,
        int ToVersion,
        List<CascadeBumpPinChange> PinChanges);
}
