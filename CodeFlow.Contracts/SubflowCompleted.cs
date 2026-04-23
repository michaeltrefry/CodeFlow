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
/// <param name="ReviewRound">1-indexed round number when the completing child was spawned by a
///   ReviewLoop parent. Tells the parent which round just finished so it can compute whether
///   rounds remain before mapping to the Exhausted port. Null for plain Subflow completions.</param>
/// <param name="TerminalPort">The effective terminal port of the child saga's last routed
///   source node — i.e. the port the saga picked when it decided to terminate. If the source
///   had a routing script, this is the script's <c>setNodePath(...)</c> choice; otherwise the
///   decision-kind-derived port name. Used by a ReviewLoop parent to compare against its
///   configured <c>LoopDecision</c> so authors can drive iteration off any port name they
///   choose (including routing-script-specified names like <c>Rejected</c> even when the
///   underlying agent Decision was <c>Completed</c>). Null for legacy completions.</param>
public sealed record SubflowCompleted(
    Guid ParentTraceId,
    Guid ParentNodeId,
    Guid ParentRoundId,
    Guid ChildTraceId,
    string OutputPortName,
    Uri OutputRef,
    IReadOnlyDictionary<string, JsonElement> SharedContext,
    AgentDecisionKind? Decision = null,
    int? ReviewRound = null,
    string? TerminalPort = null);
