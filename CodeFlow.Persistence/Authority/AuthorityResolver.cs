using CodeFlow.Runtime;
using CodeFlow.Runtime.Authority;

namespace CodeFlow.Persistence.Authority;

/// <summary>
/// Default <see cref="IAuthorityResolver"/> implementation. Assembles per-tier envelopes
/// from the data sources we already have today and delegates to
/// <see cref="EnvelopeIntersection"/> for the per-axis math.
///
/// Tier sourcing for sc-269 PR2:
/// <list type="bullet">
///   <item><description><b>Tenant</b> — <see cref="WorkflowExecutionEnvelope.NoOpinion"/>. The multi-tenant
///   pivot lives in another repo; until that lands, the tenant tier is silent. The intersection
///   algorithm passes other tiers through unchanged when a tier has null axes.</description></item>
///   <item><description><b>Workflow</b> — <see cref="WorkflowExecutionEnvelope.NoOpinion"/>. A workflow-declared
///   envelope is a future field on the workflow definition; today the workflow tier is silent.</description></item>
///   <item><description><b>Role</b> — derived from <see cref="IRoleResolutionService"/>. Today CodeFlow's
///   role grants only express tool/mcp permissions, so only <c>ToolGrants</c> is populated;
///   other axes stay null.</description></item>
///   <item><description><b>Context</b> — passed through from <see cref="ResolveAuthorityRequest.ContextTier"/>
///   when the caller has saga-level overrides to inject (e.g., a workflow input that
///   explicitly narrows repo scopes for the run).</description></item>
/// </list>
///
/// Future PRs widen the workflow and tenant tiers without changing this contract — they
/// just stop returning <see cref="WorkflowExecutionEnvelope.NoOpinion"/> and start
/// contributing real envelopes.
/// </summary>
public sealed class AuthorityResolver : IAuthorityResolver
{
    private readonly IRoleResolutionService roleResolution;

    public AuthorityResolver(IRoleResolutionService roleResolution)
    {
        ArgumentNullException.ThrowIfNull(roleResolution);
        this.roleResolution = roleResolution;
    }

    public async Task<EnvelopeResolutionResult> ResolveAsync(
        ResolveAuthorityRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var resolved = await roleResolution.ResolveAsync(request.AgentKey, cancellationToken);

        var tiers = new List<EnvelopeTier>
        {
            new(Tiers.Tenant, WorkflowExecutionEnvelope.NoOpinion),
            new(Tiers.Workflow, WorkflowExecutionEnvelope.NoOpinion),
            new(Tiers.Role, BuildRoleTier(resolved)),
            new(Tiers.Context, request.ContextTier ?? WorkflowExecutionEnvelope.NoOpinion)
        };

        return EnvelopeIntersection.Intersect(tiers);
    }

    private static WorkflowExecutionEnvelope BuildRoleTier(ResolvedAgentTools resolved)
    {
        if (resolved == ResolvedAgentTools.Empty)
        {
            return WorkflowExecutionEnvelope.NoOpinion;
        }

        var toolGrants = new List<ToolGrant>();
        foreach (var name in resolved.AllowedToolNames)
        {
            toolGrants.Add(new ToolGrant(name, ToolGrant.CategoryHost));
        }
        foreach (var mcp in resolved.McpTools)
        {
            // McpToolDefinition.FullName ("mcp:{server}:{tool}") is the public identifier;
            // if we ever change that mapping it shifts in lockstep — tests cover it.
            toolGrants.Add(new ToolGrant(mcp.FullName, ToolGrant.CategoryMcp));
        }

        if (toolGrants.Count == 0)
        {
            return WorkflowExecutionEnvelope.NoOpinion;
        }

        return WorkflowExecutionEnvelope.NoOpinion with
        {
            ToolGrants = toolGrants
        };
    }
}
