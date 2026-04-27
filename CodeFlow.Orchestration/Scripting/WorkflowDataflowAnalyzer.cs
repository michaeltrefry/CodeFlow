using System.Collections.Concurrent;
using CodeFlow.Persistence;
using Microsoft.Extensions.Caching.Memory;

namespace CodeFlow.Orchestration.Scripting;

/// <summary>
/// F2 (Workflow Authoring DX): the platform's single source of dataflow truth. Reads each
/// node's input + output scripts via Acornima, walks the workflow graph in topological order,
/// and produces a per-node <see cref="NodeDataflowScope"/> snapshot recording every workflow
/// variable / context key any reachable upstream node may have written.
///
/// Cache: per <c>(workflowKey, workflowVersion)</c>. Workflow versions are immutable so the
/// snapshot can live for the lifetime of the version. The cache is bounded; entries expire on
/// LRU pressure but are never explicitly invalidated — a new workflow version is a new cache
/// key.
///
/// Performance: parsing is the hot path. Acornima parses each script in well under 1ms for the
/// scripts seen in first-party workflows (low-hundreds-of-LoC). On a 50-node workflow this
/// stays well within the 500ms cold target stated in the F2 card.
/// </summary>
public sealed class WorkflowDataflowAnalyzer : IWorkflowDataflowAnalyzer
{
    private const int CacheSizeLimit = 256;
    private static readonly TimeSpan CacheSlidingExpiration = TimeSpan.FromMinutes(15);

    private readonly IMemoryCache cache;

    public WorkflowDataflowAnalyzer()
        : this(new MemoryCache(new MemoryCacheOptions { SizeLimit = CacheSizeLimit }))
    {
    }

    public WorkflowDataflowAnalyzer(IMemoryCache cache)
    {
        this.cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    public WorkflowDataflowSnapshot Analyze(Workflow workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);

        var key = $"f2:{workflow.Key}:{workflow.Version}";
        if (cache.TryGetValue(key, out WorkflowDataflowSnapshot? cached) && cached is not null)
        {
            return cached;
        }

        var snapshot = Compute(workflow);
        cache.Set(key, snapshot, new MemoryCacheEntryOptions
        {
            SlidingExpiration = CacheSlidingExpiration,
            Size = 1,
        });
        return snapshot;
    }

    public NodeDataflowScope? GetScope(Workflow workflow, Guid nodeId)
    {
        var snapshot = Analyze(workflow);
        return snapshot.ScopesByNode.TryGetValue(nodeId, out var scope) ? scope : null;
    }

    private static WorkflowDataflowSnapshot Compute(Workflow workflow)
    {
        var nodesById = workflow.Nodes.ToDictionary(n => n.Id);
        var predecessorEdges = BuildPredecessorMap(workflow);
        var perNodeWrites = ExtractPerNodeWrites(workflow);
        var diagnostics = perNodeWrites.SelectMany(w =>
            w.Value.Diagnostics.Select(msg => new WorkflowDataflowDiagnostic(w.Key, w.Value.ScriptKind, msg)))
            .ToArray();

        var scopes = new Dictionary<Guid, NodeDataflowScope>();
        foreach (var node in workflow.Nodes)
        {
            var ancestorIds = CollectAncestors(node.Id, predecessorEdges);

            var workflowVars = MergeWritesAcrossAncestors(
                ancestorIds,
                perNodeWrites,
                selectWrites: w => w.WorkflowWrites);

            var contextKeys = MergeWritesAcrossAncestors(
                ancestorIds,
                perNodeWrites,
                selectWrites: w => w.ContextWrites);

            var inputSource = ResolveInputSource(node, workflow, predecessorEdges);
            var loopBindings = ResolveLoopBindings(node, workflow, nodesById);

            scopes[node.Id] = new NodeDataflowScope(
                NodeId: node.Id,
                WorkflowVariables: workflowVars,
                ContextKeys: contextKeys,
                InputSource: inputSource,
                LoopBindings: loopBindings);
        }

        return new WorkflowDataflowSnapshot(
            WorkflowKey: workflow.Key,
            WorkflowVersion: workflow.Version,
            ScopesByNode: scopes,
            Diagnostics: diagnostics);
    }

    private static Dictionary<Guid, List<WorkflowEdge>> BuildPredecessorMap(Workflow workflow)
    {
        var map = new Dictionary<Guid, List<WorkflowEdge>>();
        foreach (var edge in workflow.Edges)
        {
            if (!map.TryGetValue(edge.ToNodeId, out var list))
            {
                list = new List<WorkflowEdge>();
                map[edge.ToNodeId] = list;
            }
            list.Add(edge);
        }
        return map;
    }

    /// <summary>
    /// Per-node parse of input + output scripts. Workflow scope is the union over both;
    /// the script-kind label distinguishes the source for diagnostics and source attribution.
    /// </summary>
    private static Dictionary<Guid, NodeWriteSnapshot> ExtractPerNodeWrites(Workflow workflow)
    {
        var result = new Dictionary<Guid, NodeWriteSnapshot>();
        foreach (var node in workflow.Nodes)
        {
            var inputResult = ScriptDataflowExtractor.Extract(node.InputScript, "input");
            var outputResult = ScriptDataflowExtractor.Extract(node.OutputScript, "output");

            var workflowWrites = inputResult.WorkflowWrites
                .Select(w => new ExtractedKeyWriteSourced(w, "input"))
                .Concat(outputResult.WorkflowWrites.Select(w => new ExtractedKeyWriteSourced(w, "output")))
                .ToArray();

            // P4: a node with MirrorOutputToWorkflowVar also definitively writes that key
            // before its output script runs, so downstream nodes see it.
            if (!string.IsNullOrWhiteSpace(node.MirrorOutputToWorkflowVar))
            {
                workflowWrites = workflowWrites
                    .Append(new ExtractedKeyWriteSourced(
                        new ExtractedKeyWrite(node.MirrorOutputToWorkflowVar.Trim(), DataflowConfidence.Definite),
                        "mirror"))
                    .ToArray();
            }

            // P3: ReviewLoop with rejection-history enabled writes __loop.rejectionHistory to
            // the workflow bag. Surface that as a definite source so downstream / inner-loop
            // tooling can reason about it.
            if (node.Kind == WorkflowNodeKind.ReviewLoop
                && node.RejectionHistory is { Enabled: true })
            {
                workflowWrites = workflowWrites
                    .Append(new ExtractedKeyWriteSourced(
                        new ExtractedKeyWrite(
                            RejectionHistoryAccumulator.WorkflowVariableKey,
                            DataflowConfidence.Definite),
                        "review-loop"))
                    .ToArray();
            }

            var contextWrites = inputResult.ContextWrites
                .Select(w => new ExtractedKeyWriteSourced(w, "input"))
                .Concat(outputResult.ContextWrites.Select(w => new ExtractedKeyWriteSourced(w, "output")))
                .ToArray();

            var diagnostics = inputResult.Diagnostics
                .Select(d => $"input: {d}")
                .Concat(outputResult.Diagnostics.Select(d => $"output: {d}"))
                .ToArray();

            result[node.Id] = new NodeWriteSnapshot(
                ScriptKind: "combined",
                WorkflowWrites: workflowWrites,
                ContextWrites: contextWrites,
                Diagnostics: diagnostics);
        }
        return result;
    }

    private static IReadOnlyList<DataflowVariable> MergeWritesAcrossAncestors(
        IReadOnlySet<Guid> ancestorIds,
        IReadOnlyDictionary<Guid, NodeWriteSnapshot> perNodeWrites,
        Func<NodeWriteSnapshot, IReadOnlyList<ExtractedKeyWriteSourced>> selectWrites)
    {
        var byKey = new Dictionary<string, MutableVariable>(StringComparer.Ordinal);

        foreach (var ancestorId in ancestorIds)
        {
            if (!perNodeWrites.TryGetValue(ancestorId, out var snapshot))
            {
                continue;
            }

            foreach (var sourced in selectWrites(snapshot))
            {
                if (!byKey.TryGetValue(sourced.Write.Key, out var entry))
                {
                    entry = new MutableVariable(sourced.Write.Key);
                    byKey[sourced.Write.Key] = entry;
                }

                entry.AddSource(ancestorId, sourced.OriginScriptKind, sourced.Write.Confidence);
            }
        }

        return byKey.Values
            .OrderBy(v => v.Key, StringComparer.Ordinal)
            .Select(v => v.Build())
            .ToArray();
    }

    private static HashSet<Guid> CollectAncestors(
        Guid nodeId,
        IReadOnlyDictionary<Guid, List<WorkflowEdge>> predecessors)
    {
        var ancestors = new HashSet<Guid>();
        var stack = new Stack<Guid>();
        stack.Push(nodeId);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!predecessors.TryGetValue(current, out var incoming))
            {
                continue;
            }

            foreach (var edge in incoming)
            {
                if (ancestors.Add(edge.FromNodeId))
                {
                    stack.Push(edge.FromNodeId);
                }
            }
        }
        return ancestors;
    }

    private static DataflowInputSource? ResolveInputSource(
        WorkflowNode node,
        Workflow workflow,
        IReadOnlyDictionary<Guid, List<WorkflowEdge>> predecessors)
    {
        if (node.Kind == WorkflowNodeKind.Start)
        {
            return null;
        }

        if (!predecessors.TryGetValue(node.Id, out var incoming) || incoming.Count == 0)
        {
            return null;
        }

        // For nodes with multiple inbound edges, pick the lowest-sort-order edge as the
        // primary input source. The editor can render alternates from the predecessor map
        // directly; this just gives the inspector a sensible default to highlight.
        var primary = incoming.OrderBy(e => e.SortOrder).First();
        return new DataflowInputSource(primary.FromNodeId, primary.FromPort);
    }

    private static DataflowLoopBindings? ResolveLoopBindings(
        WorkflowNode node,
        Workflow workflow,
        IReadOnlyDictionary<Guid, WorkflowNode> nodesById)
    {
        // A node "is inside a ReviewLoop" iff its workflow IS the inner subflow of some
        // ReviewLoop node. Top-level workflow analysis can't see across the parent boundary
        // — that's intentional. The editor can pass the parent context separately if it
        // wants to render the bindings on a child workflow's nodes.
        //
        // For the current workflow: the only node that exposes loop bindings to itself is a
        // ReviewLoop node, and only via its own ReviewMaxRounds. The round counter is
        // resolved at runtime per-iteration; static analysis knows only the upper bound.
        if (node.Kind != WorkflowNodeKind.ReviewLoop || node.ReviewMaxRounds is not int maxRounds)
        {
            return null;
        }

        return new DataflowLoopBindings(StaticRound: null, MaxRounds: maxRounds);
    }

    private sealed class MutableVariable
    {
        public string Key { get; }
        private readonly List<DataflowVariableSource> sources = new();
        private DataflowConfidence aggregateConfidence = DataflowConfidence.Conditional;
        private bool hasAnyDefinite;

        public MutableVariable(string key)
        {
            Key = key;
        }

        public void AddSource(Guid nodeId, string scriptKind, DataflowConfidence confidence)
        {
            sources.Add(new DataflowVariableSource(nodeId, scriptKind));
            if (confidence == DataflowConfidence.Definite)
            {
                hasAnyDefinite = true;
                aggregateConfidence = DataflowConfidence.Definite;
            }
        }

        public DataflowVariable Build() => new(
            Key: Key,
            Confidence: hasAnyDefinite ? DataflowConfidence.Definite : DataflowConfidence.Conditional,
            Sources: sources
                .OrderBy(s => s.NodeId)
                .ThenBy(s => s.ScriptKind, StringComparer.Ordinal)
                .ToArray());
    }

    private sealed record NodeWriteSnapshot(
        string ScriptKind,
        IReadOnlyList<ExtractedKeyWriteSourced> WorkflowWrites,
        IReadOnlyList<ExtractedKeyWriteSourced> ContextWrites,
        IReadOnlyList<string> Diagnostics);

    private sealed record ExtractedKeyWriteSourced(ExtractedKeyWrite Write, string OriginScriptKind);
}
