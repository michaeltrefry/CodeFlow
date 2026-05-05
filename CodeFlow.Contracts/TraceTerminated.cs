namespace CodeFlow.Contracts;

/// <summary>
/// Published by <c>WorkflowSagaStateMachine</c> the moment a saga reaches a terminal state
/// (<c>Completed</c> or <c>Failed</c>). Drives post-termination cleanup — Docker resources,
/// per-trace workdir, per-trace git-credential file — out of the saga's <c>WhenEnter</c>
/// chain and into a dedicated consumer that evaluates policy on its own. The saga itself
/// holds no opinion about which trace is eligible for which cleanup.
/// </summary>
/// <param name="TraceId">Trace id of the saga that terminated.</param>
/// <param name="ParentTraceId">Parent saga's trace id when this saga was a Subflow / ReviewLoop
///   child, otherwise null. Cleanup policy that targets shared per-trace state (workdir,
///   credential file) is gated on this being null — child sagas share the parent's resources
///   and cleanup must happen exactly once at the top of the trace tree.</param>
/// <param name="FinalState">The terminal state the saga reached: <c>Completed</c> or
///   <c>Failed</c>. The cleanup consumer uses this to differentiate "happy path" cleanup
///   (workdir + creds, gated on every repo having a PR URL) from "container cleanup runs in
///   both terminal states."</param>
/// <param name="WorkflowInputsJson">The saga's <c>WorkflowInputsJson</c> bag at termination.
///   Carries the <c>repositories</c> array — including each entry's <c>prUrl</c> — that the
///   cleanup consumer's "did every repo publish a PR?" predicate reads. Null when the trace
///   never wrote to the workflow bag (legacy traces, non-code-aware workflows).</param>
public sealed record TraceTerminated(
    Guid TraceId,
    Guid? ParentTraceId,
    string FinalState,
    string? WorkflowInputsJson);
