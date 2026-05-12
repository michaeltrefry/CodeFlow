namespace CodeFlow.Contracts;

/// <summary>
/// Per-invocation ForEach iteration context piggybacked onto <see cref="AgentInvokeRequested"/> and
/// <see cref="SubflowInvokeRequested"/> when the dispatching saga is inside a ForEach iteration
/// (sc-942 / sc-943). Each field maps directly to a top-level prompt-template variable the child
/// agent can read, mirroring the way <see cref="AgentInvokeRequested.ReviewRound"/> becomes the
/// <c>round</c> template variable.
/// </summary>
/// <param name="ItemJson">JSON-encoded payload for the current iteration's item. Exposed in the
/// child Scriban scope as <c>loop.item</c>. Strings/numbers/objects/arrays round-trip; null is
/// allowed (treated as a literal null item).</param>
/// <param name="Index">0-based iteration index. Exposed as <c>loop.index</c>.</param>
/// <param name="Count">Total iteration count snapshot taken at first dispatch. Exposed as
/// <c>loop.count</c>; used to compute <c>loop.isLast</c>.</param>
public sealed record ForEachInvocationContext(
    string ItemJson,
    int Index,
    int Count);
