using System.Text.Json;

namespace CodeFlow.Contracts;

/// <summary>
/// Emitted by a child saga when it reaches a terminal state. Drives the parent saga to resume
/// routing from the Subflow node's matching output port and merges the child's final
/// <c>workflow</c> back into the parent's <c>workflow</c> (shallow, last-write-wins per top-level
/// key) before routing.
/// </summary>
/// <param name="ParentTraceId">Trace id of the parent saga to wake.</param>
/// <param name="ParentNodeId">Id of the Subflow node on the parent workflow that fired.</param>
/// <param name="ParentRoundId">Parent's round id captured at dispatch — used for stale-round
///   rejection on the parent saga.</param>
/// <param name="ChildTraceId">Trace id of the child saga that produced this completion.</param>
/// <param name="OutputPortName">Port name on the parent's Subflow node that the parent should
///   route from. Author-defined; matches one of the child workflow's terminal ports (or the
///   implicit <c>Failed</c> port).</param>
/// <param name="OutputRef">Artifact reference produced by the child's last node — handed to
///   whatever the parent routes to next.</param>
/// <param name="WorkflowContext">The child saga's final <c>workflow</c> bag, including any
///   <c>setWorkflow</c> writes performed during the child's execution.</param>
/// <param name="Decision">The child saga's terminal port name as a string (the author-defined
///   port). Mirrors <see cref="OutputPortName"/> in the new port model and is preserved as a
///   distinct field for trace tooling. Null for legacy or synthetic completions.</param>
/// <param name="ReviewRound">1-indexed round number when the completing child was spawned by a
///   ReviewLoop parent. Tells the parent which round just finished so it can compute whether
///   rounds remain before mapping to the Exhausted port. Null for plain Subflow completions.</param>
/// <param name="TerminalPort">The effective terminal port of the child saga's last routed
///   source node — i.e. the port the saga picked when it decided to terminate. If the source
///   had a routing script, this is the script's <c>setNodePath(...)</c> choice; otherwise the
///   agent's submitted port name. Used by a ReviewLoop parent to compare against its
///   configured <c>LoopDecision</c> so authors can drive iteration off any port name they
///   choose. Null for legacy completions.</param>
public sealed record SubflowCompleted(
    Guid ParentTraceId,
    Guid ParentNodeId,
    Guid ParentRoundId,
    Guid ChildTraceId,
    string OutputPortName,
    Uri OutputRef,
    IReadOnlyDictionary<string, JsonElement> WorkflowContext,
    string? Decision = null,
    int? ReviewRound = null,
    string? TerminalPort = null);
