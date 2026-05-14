using CodeFlow.Persistence.Authority;
using CodeFlow.Runtime;
using CodeFlow.Runtime.Authority;
using FluentAssertions;

namespace CodeFlow.Persistence.Tests.Authority;

/// <summary>
/// Unit tests for <see cref="AuthorityResolver"/> against a stub <see cref="IRoleResolutionService"/>.
/// Real-DB role resolution is already covered by <c>RoleResolutionServiceTests</c>; this
/// suite focuses on the role-tier mapping (host + mcp tools) and the pass-through behavior
/// of the tenant, workflow, and context tiers.
/// </summary>
public sealed class AuthorityResolverTests
{
    [Fact]
    public async Task Resolves_HostAndMcpToolGrants_FromRoleTier()
    {
        var resolver = new AuthorityResolver(new StubRoleResolution(new ResolvedAgentTools(
            AllowedToolNames: new[] { "apply_patch", "run_command" },
            McpTools: new[]
            {
                new McpToolDefinition("github", "list_issues", "stub", null),
                new McpToolDefinition("notion", "search", "stub", null)
            },
            EnableHostTools: true)));

        var result = await resolver.ResolveAsync(new ResolveAuthorityRequest("dev", AgentVersion: 1));

        result.Envelope.ToolGrants.Should().BeEquivalentTo(new[]
        {
            new ToolGrant("apply_patch", ToolGrant.CategoryHost),
            new ToolGrant("run_command", ToolGrant.CategoryHost),
            new ToolGrant("mcp:github:list_issues", ToolGrant.CategoryMcp),
            new ToolGrant("mcp:notion:search", ToolGrant.CategoryMcp)
        });
        result.BlockedAxes.Should().BeEmpty();
    }

    [Fact]
    public async Task EmptyRole_ResolvesToNoOpinionEnvelope()
    {
        var resolver = new AuthorityResolver(new StubRoleResolution(ResolvedAgentTools.Empty));

        var result = await resolver.ResolveAsync(new ResolveAuthorityRequest("dev", AgentVersion: 1));

        result.Envelope.Should().Be(WorkflowExecutionEnvelope.NoOpinion);
        result.BlockedAxes.Should().BeEmpty();
    }

    [Fact]
    public async Task ContextTier_PassesThroughIntoIntersection()
    {
        // Context tier carries a per-trace narrowing (e.g., a saga-level repo restriction).
        // The resolver should fold it into intersection alongside the role tier.
        var resolver = new AuthorityResolver(new StubRoleResolution(new ResolvedAgentTools(
            new[] { "apply_patch" },
            Array.Empty<McpToolDefinition>(),
            EnableHostTools: true)));

        var contextEnvelope = WorkflowExecutionEnvelope.NoOpinion with
        {
            RepoScopes = new[]
            {
                new RepoScopeGrant("github.com/acme/web", "/", RepoAccess.Write)
            },
            Budget = new EnvelopeBudget(MaxTokens: 5_000)
        };

        var result = await resolver.ResolveAsync(new ResolveAuthorityRequest(
            "dev",
            AgentVersion: 1,
            ContextTier: contextEnvelope));

        result.Envelope.RepoScopes.Should().HaveCount(1);
        result.Envelope.Budget!.MaxTokens.Should().Be(5_000);
        result.Envelope.ToolGrants.Should().ContainSingle().Which.ToolName.Should().Be("apply_patch");
    }

    [Fact]
    public async Task TenantAndWorkflowTiers_SilentInPR2_DoNotEmitDenials()
    {
        // PR2 leaves tenant + workflow tiers as NoOpinion — they neither grant nor deny.
        // Intersection must not emit tier-removed denials for axes those tiers are silent on.
        var resolver = new AuthorityResolver(new StubRoleResolution(new ResolvedAgentTools(
            new[] { "apply_patch" },
            Array.Empty<McpToolDefinition>(),
            EnableHostTools: true)));

        var result = await resolver.ResolveAsync(new ResolveAuthorityRequest("dev", AgentVersion: 1));

        result.BlockedAxes.Should().BeEmpty(
            "tenant and workflow tiers contribute NoOpinion in PR2; nothing is removed.");
        result.Tiers.Should().HaveCount(4);
        result.Tiers.Select(t => t.Name).Should().BeEquivalentTo(
            new[] { Tiers.Tenant, Tiers.Workflow, Tiers.Role, Tiers.Context });
    }

    [Fact]
    public async Task ResolvedToolsOnRequest_BuildsRoleTierFromIt_WithoutReResolvingRoleGrants()
    {
        // Epic 993 / NO-7: when the consumer hands over the effective post-override tool set,
        // the resolver must build the Role tier from it — not re-resolve role grants from the
        // DB — so per-node additive tools land in the envelope.
        var stub = new StubRoleResolution(new ResolvedAgentTools(
            new[] { "role_tool" },
            Array.Empty<McpToolDefinition>(),
            EnableHostTools: true));
        var resolver = new AuthorityResolver(stub);

        var effectiveTools = new ResolvedAgentTools(
            new[] { "role_tool", "node_override_tool" },
            new[] { new McpToolDefinition("github", "list_issues", "stub", null) },
            EnableHostTools: true);

        var result = await resolver.ResolveAsync(new ResolveAuthorityRequest(
            "dev",
            AgentVersion: 1,
            ResolvedTools: effectiveTools));

        stub.ResolveAsyncCallCount.Should().Be(0, "the supplied ResolvedTools should bypass DB role resolution");
        result.Envelope.ToolGrants.Should().BeEquivalentTo(new[]
        {
            new ToolGrant("role_tool", ToolGrant.CategoryHost),
            new ToolGrant("node_override_tool", ToolGrant.CategoryHost),
            new ToolGrant("mcp:github:list_issues", ToolGrant.CategoryMcp)
        });
        result.BlockedAxes.Should().BeEmpty();
    }

    [Fact]
    public async Task NullResolvedTools_FallsBackToRoleResolution()
    {
        var stub = new StubRoleResolution(new ResolvedAgentTools(
            new[] { "role_tool" },
            Array.Empty<McpToolDefinition>(),
            EnableHostTools: true));
        var resolver = new AuthorityResolver(stub);

        var result = await resolver.ResolveAsync(new ResolveAuthorityRequest("dev", AgentVersion: 1));

        stub.ResolveAsyncCallCount.Should().Be(1, "no ResolvedTools supplied → resolver re-resolves role grants");
        result.Envelope.ToolGrants.Should().ContainSingle().Which.ToolName.Should().Be("role_tool");
    }

    [Fact]
    public async Task AdditiveOverrideTool_StillRestrictedByContextTier()
    {
        // Epic 993 / NO-7: additive node-override tools join at the Role tier — they do not
        // bypass a deliberately-narrowing higher tier. A context tier that omits the override
        // tool must still intersect it out, with denial evidence.
        var resolver = new AuthorityResolver(new StubRoleResolution(ResolvedAgentTools.Empty));

        var effectiveTools = new ResolvedAgentTools(
            new[] { "role_tool", "node_override_tool" },
            Array.Empty<McpToolDefinition>(),
            EnableHostTools: true);

        var contextEnvelope = WorkflowExecutionEnvelope.NoOpinion with
        {
            ToolGrants = new[] { new ToolGrant("role_tool", ToolGrant.CategoryHost) }
        };

        var result = await resolver.ResolveAsync(new ResolveAuthorityRequest(
            "dev",
            AgentVersion: 1,
            ContextTier: contextEnvelope,
            ResolvedTools: effectiveTools));

        result.Envelope.ToolGrants.Should().ContainSingle().Which.ToolName.Should().Be("role_tool");
        result.BlockedAxes.Should().ContainSingle(b => b.Axis == BlockedBy.Axes.ToolGrants
            && b.RequestedValue!.Contains("node_override_tool"));
    }

    private sealed class StubRoleResolution : IRoleResolutionService
    {
        private readonly ResolvedAgentTools result;

        public StubRoleResolution(ResolvedAgentTools result) => this.result = result;

        public int ResolveAsyncCallCount { get; private set; }

        public Task<ResolvedAgentTools> ResolveAsync(string agentKey, int agentVersion, CancellationToken cancellationToken = default)
        {
            ResolveAsyncCallCount++;
            return Task.FromResult(result);
        }

        public Task<ResolvedAgentTools> ResolveByRoleAsync(long roleId, CancellationToken cancellationToken = default)
            => Task.FromResult(result);

        public Task<ResolvedAgentTools> ResolveToolIdentifiersAsync(IEnumerable<string> toolIdentifiers, CancellationToken cancellationToken = default)
            => Task.FromResult(ResolvedAgentTools.Empty);
    }
}
