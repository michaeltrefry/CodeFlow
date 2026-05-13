using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CodeFlow.Orchestration.NodeDispatch;

/// <summary>
/// DI registration helper for the per-node-kind dispatcher set + the registry that resolves
/// them. Production wiring (CodeFlow.Host) and saga test harnesses both call
/// <see cref="AddWorkflowNodeDispatchers"/> so any saga that runs has the dispatchers
/// available — the saga's <c>DispatchToNodeAsync</c> resolves the registry on every dispatch.
/// </summary>
public static class NodeDispatchServiceCollectionExtensions
{
    /// <summary>
    /// Registers all built-in <see cref="IWorkflowNodeDispatcher"/> implementations
    /// (Agent / Hitl / Start / Subflow / ReviewLoop / Swarm) plus the
    /// <see cref="WorkflowNodeDispatcherRegistry"/> as singletons. Idempotent — safe to call
    /// twice; <see cref="WorkflowNodeDispatcherRegistry"/> guards against duplicate kind
    /// registration.
    /// </summary>
    public static IServiceCollection AddWorkflowNodeDispatchers(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWorkflowNodeDispatcher, AgentNodeDispatcher>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWorkflowNodeDispatcher, HitlNodeDispatcher>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWorkflowNodeDispatcher, StartNodeDispatcher>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWorkflowNodeDispatcher, SubflowNodeDispatcher>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWorkflowNodeDispatcher, ReviewLoopNodeDispatcher>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWorkflowNodeDispatcher, SwarmNodeDispatcher>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWorkflowNodeDispatcher, ForEachNodeDispatcher>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWorkflowNodeDispatcher, GoalNodeDispatcher>());
        services.TryAddSingleton<WorkflowNodeDispatcherRegistry>();

        return services;
    }
}
