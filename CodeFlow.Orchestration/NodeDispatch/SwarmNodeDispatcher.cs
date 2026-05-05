using CodeFlow.Persistence;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;

namespace CodeFlow.Orchestration.NodeDispatch;

/// <summary>
/// Dispatcher for <see cref="WorkflowNodeKind.Swarm"/>. Routes to the Sequential or
/// Coordinator entry path inside <c>WorkflowSagaStateMachine.Swarm.cs</c> based on the node's
/// configured <c>SwarmProtocol</c>. Delegates to the saga's <c>PublishSwarmEntryAsync</c>
/// helper (now <c>internal static</c>); Phase 2 will move the body — and the swarm
/// completion-routing partial — into a per-protocol class.
/// </summary>
public sealed class SwarmNodeDispatcher : IWorkflowNodeDispatcher
{
    public WorkflowNodeKind Kind => WorkflowNodeKind.Swarm;

    public Task DispatchAsync(NodeDispatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var services = request.Context.GetPayload<IServiceProvider>();
        return WorkflowSagaStateMachine.PublishSwarmEntryAsync(
            request.Context,
            services.GetRequiredService<IAgentConfigRepository>(),
            services.GetRequiredService<IArtifactStore>(),
            request.Saga,
            request.Workflow,
            request.Node,
            request.InputRef,
            request.RoundId);
    }
}
