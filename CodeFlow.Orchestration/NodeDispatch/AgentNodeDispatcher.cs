using CodeFlow.Orchestration.Scripting;
using CodeFlow.Persistence;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;

namespace CodeFlow.Orchestration.NodeDispatch;

/// <summary>
/// Dispatcher for <see cref="WorkflowNodeKind.Agent"/>, <see cref="WorkflowNodeKind.Hitl"/>,
/// and <see cref="WorkflowNodeKind.Start"/> — all kinds whose entry produces an
/// <see cref="CodeFlow.Contracts.AgentInvokeRequested"/> message. Delegates to the saga's
/// existing <c>PublishHandoffAsync</c> helper (now <c>internal static</c>); Phase 2 will move
/// the body here so per-kind logic owns its own home.
/// </summary>
public sealed class AgentNodeDispatcher : IWorkflowNodeDispatcher
{
    public WorkflowNodeKind Kind => WorkflowNodeKind.Agent;

    public Task DispatchAsync(NodeDispatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var services = request.Context.GetPayload<IServiceProvider>();
        return WorkflowSagaStateMachine.PublishHandoffAsync(
            request.Context,
            services.GetRequiredService<IAgentConfigRepository>(),
            services.GetRequiredService<LogicNodeScriptHost>(),
            services.GetRequiredService<IArtifactStore>(),
            request.Saga,
            request.Workflow,
            request.Node,
            request.InputRef,
            request.RoundId,
            request.RetryContext);
    }
}

/// <summary>HITL nodes share the agent-invocation publish path; the worker fork keys on the
/// node's role grants rather than its kind.</summary>
public sealed class HitlNodeDispatcher : IWorkflowNodeDispatcher
{
    public WorkflowNodeKind Kind => WorkflowNodeKind.Hitl;

    public Task DispatchAsync(NodeDispatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var services = request.Context.GetPayload<IServiceProvider>();
        return WorkflowSagaStateMachine.PublishHandoffAsync(
            request.Context,
            services.GetRequiredService<IAgentConfigRepository>(),
            services.GetRequiredService<LogicNodeScriptHost>(),
            services.GetRequiredService<IArtifactStore>(),
            request.Saga,
            request.Workflow,
            request.Node,
            request.InputRef,
            request.RoundId,
            request.RetryContext);
    }
}

/// <summary>Start nodes are agents under the hood — the dispatcher exists for the registry
/// shape and so adding Start-only behavior in the future has an obvious home.</summary>
public sealed class StartNodeDispatcher : IWorkflowNodeDispatcher
{
    public WorkflowNodeKind Kind => WorkflowNodeKind.Start;

    public Task DispatchAsync(NodeDispatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var services = request.Context.GetPayload<IServiceProvider>();
        return WorkflowSagaStateMachine.PublishHandoffAsync(
            request.Context,
            services.GetRequiredService<IAgentConfigRepository>(),
            services.GetRequiredService<LogicNodeScriptHost>(),
            services.GetRequiredService<IArtifactStore>(),
            request.Saga,
            request.Workflow,
            request.Node,
            request.InputRef,
            request.RoundId,
            request.RetryContext);
    }
}
