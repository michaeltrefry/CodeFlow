using CodeFlow.Persistence;

namespace CodeFlow.Orchestration.NodeDispatch;

/// <summary>
/// Lookup table from <see cref="WorkflowNodeKind"/> to the dispatcher that handles it.
/// Resolved by <see cref="WorkflowSagaStateMachine"/> from the saga's request services and
/// asked for the right dispatcher by kind. <see cref="WorkflowNodeKind.Logic"/> is intentionally
/// absent — Logic nodes are resolved by the logic-chain resolver upstream of dispatch and never
/// reach this surface; <see cref="GetForDispatch"/> throws if asked for one, surfacing any
/// regression as a clear stack trace rather than silent misrouting.
/// </summary>
public sealed class WorkflowNodeDispatcherRegistry
{
    private readonly IReadOnlyDictionary<WorkflowNodeKind, IWorkflowNodeDispatcher> dispatchers;

    public WorkflowNodeDispatcherRegistry(IEnumerable<IWorkflowNodeDispatcher> dispatchers)
    {
        ArgumentNullException.ThrowIfNull(dispatchers);

        var byKind = new Dictionary<WorkflowNodeKind, IWorkflowNodeDispatcher>();
        foreach (var dispatcher in dispatchers)
        {
            if (byKind.ContainsKey(dispatcher.Kind))
            {
                throw new InvalidOperationException(
                    $"Multiple IWorkflowNodeDispatcher implementations registered for {dispatcher.Kind}. "
                    + "Each node kind must have exactly one dispatcher.");
            }
            byKind[dispatcher.Kind] = dispatcher;
        }

        this.dispatchers = byKind;
    }

    /// <summary>
    /// Resolve the dispatcher for <paramref name="kind"/>. Throws if no dispatcher is
    /// registered (unknown kind) or if the kind is <see cref="WorkflowNodeKind.Logic"/>
    /// (those should have been resolved by the logic-chain resolver before reaching dispatch).
    /// </summary>
    public IWorkflowNodeDispatcher GetForDispatch(WorkflowNodeKind kind)
    {
        if (kind == WorkflowNodeKind.Logic)
        {
            throw new InvalidOperationException(
                "Logic nodes should have been resolved by the logic chain resolver before reaching DispatchToNodeAsync.");
        }

        if (!dispatchers.TryGetValue(kind, out var dispatcher))
        {
            throw new InvalidOperationException($"Unknown workflow node kind: {kind}.");
        }

        return dispatcher;
    }
}
