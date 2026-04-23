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
/// <param name="SharedContext">Snapshot of the parent's <c>global</c> bag at the moment of
///   dispatch. Becomes the child's working <c>global</c>.</param>
/// <param name="Depth">Subflow nesting depth assigned to the child (parent depth + 1). Capped
///   by the orchestration layer at the configured maximum to prevent runaway recursion.</param>
public sealed record SubflowInvokeRequested(
    Guid ParentTraceId,
    Guid ParentNodeId,
    Guid ParentRoundId,
    Guid ChildTraceId,
    string SubflowKey,
    int SubflowVersion,
    Uri InputRef,
    IReadOnlyDictionary<string, JsonElement> SharedContext,
    int Depth);
