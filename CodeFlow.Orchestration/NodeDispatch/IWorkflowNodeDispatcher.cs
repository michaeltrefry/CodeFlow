using CodeFlow.Persistence;
using MassTransit;

namespace CodeFlow.Orchestration.NodeDispatch;

/// <summary>
/// Per-node-kind dispatch surface. Owns the "how to enter this node" side-effect — publishing
/// the right Requested message (agent invocation, subflow invocation, swarm entry, …),
/// running the node's input script, pinning agent versions, and stamping per-trace state on
/// the saga before the next leg of the workflow starts.
///
/// <para>
/// sc-165 / F-001+F-002 — Phase 1. Replaces the per-kind switch inside
/// <see cref="WorkflowSagaStateMachine.DispatchToNodeAsync"/> with a registry-based dispatch
/// so each new node kind lands as a new dispatcher class, not as another arm of the switch.
/// Subsequent phases will move per-kind completion-routing logic into these dispatchers and
/// have <c>DryRunExecutor</c> swap the registry's "publish handoff" implementation for an
/// in-memory recorder.
/// </para>
/// </summary>
public interface IWorkflowNodeDispatcher
{
    /// <summary>
    /// The single <see cref="WorkflowNodeKind"/> this dispatcher handles. The registry uses
    /// this for the kind → dispatcher lookup.
    /// </summary>
    WorkflowNodeKind Kind { get; }

    /// <summary>
    /// Dispatch the node identified by <see cref="NodeDispatchRequest.Node"/> on the saga.
    /// Implementations are expected to publish a Requested message via
    /// <see cref="ConsumeContext.Publish{T}(T)"/> on the request's context, update the saga's
    /// per-trace state (input ref, pinned agent version), and return without waiting for the
    /// downstream worker to consume the message.
    /// </summary>
    Task DispatchAsync(NodeDispatchRequest request);
}

/// <summary>
/// Per-call inputs to a <see cref="IWorkflowNodeDispatcher"/>. A record so the saga's
/// <c>DispatchToNodeAsync</c> can construct it inline without a positional-argument blowup.
/// </summary>
/// <param name="Context">Active MassTransit behavior context — used to publish messages and
///   resolve scoped services via <see cref="MassTransit.MessageBindContextExtensions"/>.</param>
/// <param name="Saga">The saga state being mutated as part of this dispatch.</param>
/// <param name="Workflow">The workflow definition the saga is executing. Used by some
///   dispatchers (Subflow / ReviewLoop) for child-saga lookups + author-time validation
///   reachable from the node.</param>
/// <param name="Node">The node being dispatched. <see cref="WorkflowNode.Kind"/> matches the
///   dispatcher's <see cref="IWorkflowNodeDispatcher.Kind"/>.</param>
/// <param name="InputRef">The artifact URI that becomes the dispatched node's input. May be
///   rewritten by the node's input script before publish; the unmodified caller-provided ref
///   is what flows in.</param>
/// <param name="RoundId">The round id assigned to this dispatch — distinct from the saga's
///   current round id when <c>RotatesRound</c> on the inbound edge is true.</param>
/// <param name="RetryContext">Optional retry state lifted from the upstream agent
///   completion. Threaded through to <see cref="CodeFlow.Contracts.AgentInvokeRequested"/>
///   for Agent/Hitl/Start dispatch; ignored by other kinds.</param>
public sealed record NodeDispatchRequest(
    BehaviorContext<WorkflowSagaStateEntity> Context,
    WorkflowSagaStateEntity Saga,
    Workflow Workflow,
    WorkflowNode Node,
    Uri InputRef,
    Guid RoundId,
    CodeFlow.Contracts.RetryContext? RetryContext);
