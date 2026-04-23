using System.Text.Json;

namespace CodeFlow.Contracts;

/// <summary>
/// Emitted by a child saga when it reaches a terminal state. Drives the parent saga to resume
/// routing from the Subflow node's matching output port and merges the child's final
/// <c>global</c> back into the parent's <c>global</c> (shallow, last-write-wins per top-level
/// key) before routing.
/// </summary>
/// <param name="ParentTraceId">Trace id of the parent saga to wake.</param>
/// <param name="ParentNodeId">Id of the Subflow node on the parent workflow that fired.</param>
/// <param name="ParentRoundId">Parent's round id captured at dispatch — used for stale-round
///   rejection on the parent saga.</param>
/// <param name="ChildTraceId">Trace id of the child saga that produced this completion.</param>
/// <param name="OutputPortName">Port name on the parent's Subflow node that the parent should
///   route from. One of <c>Completed</c>, <c>Failed</c>, or <c>Escalated</c> — fixed by the
///   Subflow node's port set.</param>
/// <param name="OutputRef">Artifact reference produced by the child's last node — handed to
///   whatever the parent routes to next.</param>
/// <param name="SharedContext">The child saga's final <c>global</c> bag, including any
///   <c>setGlobal</c> writes performed during the child's execution.</param>
/// <param name="Decision">The child saga's terminal <see cref="AgentDecisionKind"/>. Included so
///   a ReviewLoop parent can drive its outcome mapping (Approved/Completed → Approved port,
///   Rejected → next round or Exhausted port, Failed/Escalated → Failed port) without re-fetching
///   the child's last decision from storage. Null for legacy or synthetic completions.</param>
public sealed record SubflowCompleted(
    Guid ParentTraceId,
    Guid ParentNodeId,
    Guid ParentRoundId,
    Guid ChildTraceId,
    string OutputPortName,
    Uri OutputRef,
    IReadOnlyDictionary<string, JsonElement> SharedContext,
    AgentDecisionKind? Decision = null);
