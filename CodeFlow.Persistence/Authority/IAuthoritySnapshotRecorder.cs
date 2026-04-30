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
    int? AgentVersion = null,
    string? WorkflowKey = null,
    int? WorkflowVersion = null,
    WorkflowExecutionEnvelope? ContextTier = null);
