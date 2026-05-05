using CodeFlow.Orchestration.Scripting;
using CodeFlow.Persistence;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;

namespace CodeFlow.Orchestration.NodeDispatch;

/// <summary>
/// Dispatcher for <see cref="WorkflowNodeKind.Subflow"/>. Publishes a
/// <see cref="CodeFlow.Contracts.SubflowInvokeRequested"/> for the child workflow, runs the
/// node's input script if any, and updates the parent saga's <c>CurrentInputRef</c>.
/// Delegates to the saga's <c>PublishSubflowDispatchAsync</c> helper (now
/// <c>internal static</c>) which is also called from <c>RouteSubflowCompletionAsync</c>'s
/// ReviewLoop iterate path with <c>runInputScript: false</c>.
/// </summary>
public sealed class SubflowNodeDispatcher : IWorkflowNodeDispatcher
{
    public WorkflowNodeKind Kind => WorkflowNodeKind.Subflow;

    public Task DispatchAsync(NodeDispatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var services = request.Context.GetPayload<IServiceProvider>();
        return WorkflowSagaStateMachine.PublishSubflowDispatchAsync(
            request.Context,
            services.GetRequiredService<LogicNodeScriptHost>(),
            services.GetRequiredService<IArtifactStore>(),
            request.Saga,
            request.Workflow,
            request.Node,
            request.InputRef,
            request.RoundId);
    }
}

/// <summary>
/// Dispatcher for <see cref="WorkflowNodeKind.ReviewLoop"/>. Same publish path as Subflow,
/// but seeds the child saga with round 1 of N and the parent's configured loop decision so
/// the child's terminal port can be evaluated against the loop policy when it completes.
/// </summary>
public sealed class ReviewLoopNodeDispatcher : IWorkflowNodeDispatcher
{
    public WorkflowNodeKind Kind => WorkflowNodeKind.ReviewLoop;

    public Task DispatchAsync(NodeDispatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var node = request.Node;
        if (node.ReviewMaxRounds is not int maxRounds)
        {
            throw new InvalidOperationException(
                $"ReviewLoop node {node.Id} in workflow {request.Workflow.Key} v{request.Workflow.Version} "
                + "has no ReviewMaxRounds configured.");
        }

        var services = request.Context.GetPayload<IServiceProvider>();
        return WorkflowSagaStateMachine.PublishSubflowDispatchAsync(
            request.Context,
            services.GetRequiredService<LogicNodeScriptHost>(),
            services.GetRequiredService<IArtifactStore>(),
            request.Saga,
            request.Workflow,
            node,
            request.InputRef,
            request.RoundId,
            reviewRound: 1,
            reviewMaxRounds: maxRounds,
            loopDecision: WorkflowSagaStateMachine.ResolveLoopDecision(node));
    }
}
