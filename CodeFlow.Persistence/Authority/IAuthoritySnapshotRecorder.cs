using CodeFlow.Contracts;
using CodeFlow.Runtime;
using CodeFlow.Runtime.Authority;

namespace CodeFlow.Persistence.Authority;

/// <summary>
/// Records the per-invocation authority envelope snapshot and fires admission refusals for
/// each blocked axis. Owned separately from <see cref="IAuthorityResolver"/> so the resolver
/// stays pure (input → result) and side-effecting concerns (DB write, refusal emission) are
/// testable in isolation.
/// </summary>
public interface IAuthoritySnapshotRecorder
{
    Task<EnvelopeResolutionResult> ResolveAndRecordAsync(
        AuthoritySnapshotInput input,
        CancellationToken cancellationToken = default);
}

public sealed record AuthoritySnapshotInput(
    string AgentKey,
    Guid TraceId,
    Guid RoundId,
    int AgentVersion,
    string? WorkflowKey = null,
    int? WorkflowVersion = null,
    WorkflowExecutionEnvelope? ContextTier = null,
    ResolvedAgentTools? ResolvedTools = null,
    // Epic 993 / NO-10: the dispatching node's per-node agent overrides, captured on the
    // snapshot so the trace inspector can show what the round actually ran with.
    AgentInvocationOverrides? AgentOverrides = null);
