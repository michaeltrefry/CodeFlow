namespace CodeFlow.Runtime.Authority;

/// <summary>
/// Resolves the per-run authority envelope by combining tier inputs (tenant policy,
/// workflow declaration, agent role grants, runtime context). Implementations gather each
/// tier's envelope contribution and call <see cref="EnvelopeIntersection.Intersect"/> to
/// produce the resolved snapshot plus per-axis denial evidence.
///
/// Default implementation lives in <c>CodeFlow.Persistence</c> (next PR) so it can read
/// role grants from the DB. Tests use a stub or pass tiers directly to
/// <see cref="EnvelopeIntersection"/>.
/// </summary>
public interface IAuthorityResolver
{
    Task<EnvelopeResolutionResult> ResolveAsync(
        ResolveAuthorityRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Inputs the resolver needs to assemble each tier. Kept lightweight — anything more is
/// resolved by the implementation from its own data sources.
/// </summary>
/// <param name="AgentKey">Agent invocation target; drives role grant lookup.</param>
/// <param name="TraceId">Workflow trace id, when known. Surfaces on emitted refusal evidence.</param>
/// <param name="WorkflowKey">Workflow key, when known. Reserved for sourcing the workflow tier in a future PR.</param>
/// <param name="WorkflowVersion">Workflow version pin, when known.</param>
/// <param name="ContextTier">
/// Optional pre-built envelope for the runtime context tier (per-trace overrides such as a
/// repo restriction the saga itself wants to enforce). Resolver passes through unchanged.
/// </param>
public sealed record ResolveAuthorityRequest(
    string AgentKey,
    Guid? TraceId = null,
    string? WorkflowKey = null,
    int? WorkflowVersion = null,
    WorkflowExecutionEnvelope? ContextTier = null);
