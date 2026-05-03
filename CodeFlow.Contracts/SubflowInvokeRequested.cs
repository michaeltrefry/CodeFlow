using System.Text.Json;

namespace CodeFlow.Contracts;

/// <summary>
/// Emitted when a parent saga reaches a Subflow node. Spawns a child saga whose
/// <see cref="ChildTraceId"/> becomes the correlation key for the child workflow's lifetime.
/// </summary>
/// <param name="ParentTraceId">Trace id of the parent saga that emitted the request.</param>
/// <param name="ParentNodeId">Id of the Subflow node on the parent workflow.</param>
/// <param name="ParentRoundId">Parent's round id at dispatch — echoed back by
///   <see cref="SubflowCompleted"/> so the parent saga can drop stale completions.</param>
/// <param name="ChildTraceId">Trace id assigned to the new child saga.</param>
/// <param name="SubflowKey">Workflow key of the child workflow to invoke.</param>
/// <param name="SubflowVersion">Pinned version of the child workflow. Must be non-null at
///   dispatch — "latest at save" sentinels are resolved at parent-workflow save time.</param>
/// <param name="InputRef">Artifact reference handed to the child workflow's Start node.</param>
/// <param name="WorkflowContext">Snapshot of the parent's <c>workflow</c> bag at the moment of
///   dispatch. Becomes the child's working <c>workflow</c>.</param>
/// <param name="Depth">Subflow nesting depth assigned to the child (parent depth + 1). Capped
///   by the orchestration layer at the configured maximum to prevent runaway recursion.</param>
/// <param name="ReviewRound">1-indexed round number when this invocation is an iteration of a
///   ReviewLoop parent node. Null for plain Subflow invocations. Drives <c>{{round}}</c> /
///   <c>{{isLastRound}}</c> bindings exposed to the child workflow.</param>
/// <param name="ReviewMaxRounds">Snapshot of the ReviewLoop parent's <c>MaxRounds</c> setting at
///   dispatch. Null for plain Subflow invocations. Paired with <see cref="ReviewRound"/>.</param>
/// <param name="LoopDecision">Snapshot of the ReviewLoop parent's <c>LoopDecision</c> setting at
///   dispatch — the port name that should trigger another iteration when the child's effective
///   terminal port matches. Also lets the child saga recognize this custom port name as a legal
///   clean exit alongside the standard Completed/Approved/Rejected allowlist. Null for plain
///   Subflow invocations.</param>
/// <param name="Repositories">Snapshot of the parent saga's per-trace repository allowlist. The
///   child saga seeds its own <c>RepositoriesJson</c> from this at init, so vcs_* tools inside
///   the subflow see the same allowed repos as the parent without the child workflow having to
///   redeclare them. Null only on legacy in-flight messages produced before the contract added
///   the field.</param>
public sealed record SubflowInvokeRequested(
    Guid ParentTraceId,
    Guid ParentNodeId,
    Guid ParentRoundId,
    Guid ChildTraceId,
    string SubflowKey,
    int SubflowVersion,
    Uri InputRef,
    IReadOnlyDictionary<string, JsonElement> WorkflowContext,
    int Depth,
    int? ReviewRound = null,
    int? ReviewMaxRounds = null,
    string? LoopDecision = null,
    IReadOnlyList<RepositoryDeclaration>? Repositories = null);
