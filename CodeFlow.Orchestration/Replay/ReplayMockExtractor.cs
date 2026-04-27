using System.Text.Json.Nodes;
using CodeFlow.Orchestration.DryRun;
using CodeFlow.Persistence;

namespace CodeFlow.Orchestration.Replay;

/// <summary>
/// Lifts the recorded responses on a saga (plus its descendant subflow sagas) into the per-agent
/// mock queues the dry-run executor consumes. The walk follows the same recursion order
/// <see cref="WorkflowSagaStateMachine"/> uses at runtime so that when an agent runs in multiple
/// subflow boundaries its responses sit in the queue in the order DryRunExecutor will dequeue them:
///
/// <para>For each saga, decisions are processed in <see cref="WorkflowSagaDecisionEntity.Ordinal"/>
/// order. A "synthetic" subflow/ReviewLoop decision (an entry written by the parent when a child
/// terminates — see <c>WorkflowSagaStateMachine.BuildSubflowSyntheticAgentKey</c>) is dropped from
/// the mock queue, but immediately before it is dropped the matching child saga's decisions are
/// recursively walked. This produces the depth-first agent-visit order the executor follows.</para>
///
/// HITL decisions are emitted with the same shape as agent decisions (the saga publishes
/// <c>AgentInvocationCompleted</c> on submit and stores a row in the same decisions table), so they
/// are queued and edited identically.
/// </summary>
public static class ReplayMockExtractor
{
    /// <summary>
    /// Build the mock bundle for a replay run. The caller is responsible for loading every saga
    /// in the subtree (root + descendants) and every decision row keyed by those sagas; the
    /// extractor performs no I/O beyond resolving each row's <c>OutputRef</c> through
    /// <paramref name="artifactStore"/>.
    /// </summary>
    public static async Task<ReplayMockBundle> ExtractAsync(
        WorkflowSagaStateEntity rootSaga,
        IReadOnlyList<WorkflowSagaStateEntity> subtreeSagas,
        IReadOnlyList<WorkflowSagaDecisionEntity> allDecisions,
        IArtifactStore artifactStore,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rootSaga);
        ArgumentNullException.ThrowIfNull(subtreeSagas);
        ArgumentNullException.ThrowIfNull(allDecisions);
        ArgumentNullException.ThrowIfNull(artifactStore);

        var sagasByCorrelation = subtreeSagas.ToDictionary(s => s.CorrelationId);
        var decisionsBySaga = allDecisions
            .GroupBy(d => d.SagaCorrelationId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<WorkflowSagaDecisionEntity>)g.OrderBy(d => d.Ordinal).ToArray());

        // Pre-index each saga's spawn fingerprint (parent_node_id, parent_round_id) so the
        // recursive walk can find the child saga associated with a synthetic subflow decision in
        // O(1). Top-level sagas have no parent and are not indexed.
        var childIndex = new Dictionary<(Guid TraceId, Guid NodeId, Guid RoundId), WorkflowSagaStateEntity>();
        foreach (var saga in subtreeSagas)
        {
            if (saga.ParentTraceId is null || saga.ParentNodeId is null || saga.ParentRoundId is null)
            {
                continue;
            }

            var key = (saga.ParentTraceId.Value, saga.ParentNodeId.Value, saga.ParentRoundId.Value);
            childIndex[key] = saga;
        }

        var perAgentQueues = new Dictionary<string, List<DryRunMockResponse>>(StringComparer.Ordinal);
        var decisionRefs = new List<RecordedDecisionRef>();

        await WalkSagaAsync(
            rootSaga,
            decisionsBySaga,
            sagasByCorrelation,
            childIndex,
            perAgentQueues,
            decisionRefs,
            artifactStore,
            cancellationToken).ConfigureAwait(false);

        var mocks = perAgentQueues.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<DryRunMockResponse>)kv.Value.ToArray(),
            StringComparer.Ordinal);

        return new ReplayMockBundle(mocks, decisionRefs);
    }

    private static async Task WalkSagaAsync(
        WorkflowSagaStateEntity saga,
        IReadOnlyDictionary<Guid, IReadOnlyList<WorkflowSagaDecisionEntity>> decisionsBySaga,
        IReadOnlyDictionary<Guid, WorkflowSagaStateEntity> sagasByCorrelation,
        IReadOnlyDictionary<(Guid, Guid, Guid), WorkflowSagaStateEntity> childIndex,
        Dictionary<string, List<DryRunMockResponse>> perAgentQueues,
        List<RecordedDecisionRef> decisionRefs,
        IArtifactStore artifactStore,
        CancellationToken cancellationToken)
    {
        if (!decisionsBySaga.TryGetValue(saga.CorrelationId, out var decisions))
        {
            return;
        }

        foreach (var decision in decisions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (IsSyntheticSubflowAgentKey(decision.AgentKey))
            {
                // The synthetic decision marks where the parent saga waited on a child. Recurse
                // into the matching child saga first so its agent decisions land in the queues
                // before any decisions the parent records after the subflow returns.
                if (decision.NodeId is Guid parentNodeId
                    && childIndex.TryGetValue((saga.TraceId, parentNodeId, decision.RoundId), out var child)
                    && sagasByCorrelation.ContainsKey(child.CorrelationId))
                {
                    await WalkSagaAsync(
                        child, decisionsBySaga, sagasByCorrelation, childIndex,
                        perAgentQueues, decisionRefs, artifactStore, cancellationToken).ConfigureAwait(false);
                }

                continue;
            }

            var output = await ReadArtifactAsTextOrEmptyAsync(
                artifactStore, decision.OutputRef, cancellationToken).ConfigureAwait(false);
            var payload = ParsePayload(decision.DecisionPayloadJson);

            if (!perAgentQueues.TryGetValue(decision.AgentKey, out var queue))
            {
                queue = new List<DryRunMockResponse>();
                perAgentQueues[decision.AgentKey] = queue;
            }

            queue.Add(new DryRunMockResponse(decision.Decision, output, payload));
            decisionRefs.Add(new RecordedDecisionRef(
                AgentKey: decision.AgentKey,
                OrdinalPerAgent: queue.Count,
                SagaCorrelationId: saga.CorrelationId,
                SagaOrdinal: decision.Ordinal,
                NodeId: decision.NodeId,
                RoundId: decision.RoundId,
                OriginalDecision: decision.Decision));
        }
    }

    /// <summary>
    /// Mirrors <c>WorkflowSagaStateMachine.BuildSubflowSyntheticAgentKey</c>: the parent saga
    /// records a decision with one of these synthetic keys whenever a Subflow/ReviewLoop child
    /// terminates. Replay drops these from the mock queue because the dry-run executor walks the
    /// child workflow itself and re-derives the synthetic decision shape from the child's terminal
    /// port.
    /// </summary>
    public static bool IsSyntheticSubflowAgentKey(string? agentKey)
    {
        if (string.IsNullOrEmpty(agentKey))
        {
            return false;
        }

        return agentKey == "subflow"
            || agentKey == "review-loop"
            || agentKey.StartsWith("subflow:", StringComparison.Ordinal)
            || agentKey.StartsWith("review-loop:", StringComparison.Ordinal);
    }

    private static async Task<string?> ReadArtifactAsTextOrEmptyAsync(
        IArtifactStore artifactStore,
        string? outputRef,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(outputRef))
        {
            return null;
        }

        if (!Uri.TryCreate(outputRef, UriKind.Absolute, out var uri))
        {
            return null;
        }

        await using var stream = await artifactStore.ReadAsync(uri, cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, leaveOpen: false);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static JsonNode? ParsePayload(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(payloadJson);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
