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

        var result = await resolver.ResolveAsync(new ResolveAuthorityRequest("dev"));

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

        var result = await resolver.ResolveAsync(new ResolveAuthorityRequest("dev"));

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

        var result = await resolver.ResolveAsync(new ResolveAuthorityRequest("dev"));

        result.BlockedAxes.Should().BeEmpty(
            "tenant and workflow tiers contribute NoOpinion in PR2; nothing is removed.");
        result.Tiers.Should().HaveCount(4);
        result.Tiers.Select(t => t.Name).Should().BeEquivalentTo(
            new[] { Tiers.Tenant, Tiers.Workflow, Tiers.Role, Tiers.Context });
    }

    private sealed class StubRoleResolution : IRoleResolutionService
    {
        private readonly ResolvedAgentTools result;

        public StubRoleResolution(ResolvedAgentTools result) => this.result = result;

        public Task<ResolvedAgentTools> ResolveAsync(string agentKey, CancellationToken cancellationToken = default)
            => Task.FromResult(result);

        public Task<ResolvedAgentTools> ResolveByRoleAsync(long roleId, CancellationToken cancellationToken = default)
            => Task.FromResult(result);
    }
}
