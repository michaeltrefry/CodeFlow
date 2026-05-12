using CodeFlow.Contracts;
using CodeFlow.Persistence;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace CodeFlow.Orchestration.NodeDispatch;

/// <summary>
/// Dispatcher for <see cref="WorkflowNodeKind.ForEach"/> (sc-942 / sc-943). On first entry it
/// evaluates the node's <c>CollectionExpression</c> against the saga's workflow context, snapshots
/// the resulting JSON array onto the saga as iteration state, and dispatches the first iteration
/// as a Subflow invocation seeded with the matching <see cref="ForEachInvocationContext"/>.
///
/// Per-iteration completion is handled in <see cref="WorkflowSagaStateMachine"/>'s partial
/// <c>ForEach</c> file — this dispatcher is only responsible for the very first dispatch.
/// </summary>
public sealed class ForEachNodeDispatcher : IWorkflowNodeDispatcher
{
    public WorkflowNodeKind Kind => WorkflowNodeKind.ForEach;

    public Task DispatchAsync(NodeDispatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var services = request.Context.GetPayload<IServiceProvider>();
        return WorkflowSagaStateMachine.PublishForEachFirstDispatchAsync(
            request.Context,
            services.GetRequiredService<Scripting.LogicNodeScriptHost>(),
            services.GetRequiredService<IArtifactStore>(),
            request.Saga,
            request.Workflow,
            request.Node,
            request.InputRef,
            request.RoundId);
    }
}
