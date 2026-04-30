using CodeFlow.Runtime.Authority;
using FluentAssertions;

namespace CodeFlow.Runtime.Tests.Authority;

/// <summary>
/// Per-axis intersection tests for <see cref="EnvelopeIntersection.Intersect"/>. Covers each
/// axis's "no opinion → pass through", "intersect across tiers", and "denial evidence on
/// removal/narrowing/conflict" rules from sc-269.
/// </summary>
public sealed class EnvelopeIntersectionTests
{
    [Fact]
    public void NoTiers_ResolvesToAllNullAxes_NoBlocks()
    {
        var result = EnvelopeIntersection.Intersect(Array.Empty<EnvelopeTier>());

        result.Envelope.Should().Be(WorkflowExecutionEnvelope.NoOpinion);
        result.BlockedAxes.Should().BeEmpty();
    }

    [Fact]
    public void SingleTier_PassesEnvelopeThrough_WithoutBlocks()
    {
        var envelope = WorkflowExecutionEnvelope.NoOpinion with
        {
            ToolGrants = new[] { new ToolGrant("apply_patch", ToolGrant.CategoryHost) }
        };

        var result = EnvelopeIntersection.Intersect(new[] { new EnvelopeTier(Tiers.Role, envelope) });

        result.Envelope.ToolGrants.Should().BeEquivalentTo(envelope.ToolGrants);
        result.BlockedAxes.Should().BeEmpty();
    }

    // ---- Set axes ----------------------------------------------------------

    [Fact]
    public void RepoScopes_IntersectsSetsAcrossTiers()
    {
        var tenant = Tier(Tiers.Tenant, repoScopes: new[]
        {
            new RepoScopeGrant("github.com/acme/web", "/", RepoAccess.Read),
            new RepoScopeGrant("github.com/acme/api", "/", RepoAccess.Write)
        });
        var role = Tier(Tiers.Role, repoScopes: new[]
        {
            new RepoScopeGrant("github.com/acme/web", "/", RepoAccess.Read)
        });

        var result = EnvelopeIntersection.Intersect(new[] { tenant, role });

        result.Envelope.RepoScopes.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new RepoScopeGrant("github.com/acme/web", "/", RepoAccess.Read));
        result.BlockedAxes.Should().ContainSingle(b =>
            b.Tier == Tiers.Role
            && b.Axis == BlockedBy.Axes.RepoScopes
            && b.Code == BlockedBy.Codes.TierRemoved);
    }

    [Fact]
    public void NullSetAxis_OnTier_PassesOtherTiersValuesThrough()
    {
        // Tenant is silent (null) on tools; role grants apply_patch. Resolved = role's grants,
        // no denial evidence (silent tier expressed no opinion).
        var tenant = Tier(Tiers.Tenant);
        var role = Tier(Tiers.Role, toolGrants: new[] { new ToolGrant("apply_patch", ToolGrant.CategoryHost) });

        var result = EnvelopeIntersection.Intersect(new[] { tenant, role });

        result.Envelope.ToolGrants.Should().ContainSingle().Which.ToolName.Should().Be("apply_patch");
        result.BlockedAxes.Should().BeEmpty();
    }

    [Fact]
    public void ToolGrants_DeniedByTenant_FiresTierRemovedDenial()
    {
        var tenant = Tier(Tiers.Tenant, toolGrants: Array.Empty<ToolGrant>());
        var role = Tier(Tiers.Role, toolGrants: new[]
        {
            new ToolGrant("apply_patch", ToolGrant.CategoryHost),
            new ToolGrant("run_command", ToolGrant.CategoryHost)
        });

        var result = EnvelopeIntersection.Intersect(new[] { tenant, role });

        result.Envelope.ToolGrants.Should().BeEmpty();
        result.BlockedAxes.Should().HaveCount(2);
        result.BlockedAxes.Should().OnlyContain(b =>
            b.Tier == Tiers.Tenant
            && b.Axis == BlockedBy.Axes.ToolGrants
            && b.Code == BlockedBy.Codes.TierRemoved);
    }

    // ---- Network -----------------------------------------------------------

    [Fact]
    public void Network_MostRestrictivePolicyWins()
    {
        var tenant = Tier(Tiers.Tenant, network: new EnvelopeNetwork(NetworkPolicy.Loopback));
        var role = Tier(Tiers.Role, network: new EnvelopeNetwork(NetworkPolicy.Allowlist, new[] { "api.openai.com" }));

        var result = EnvelopeIntersection.Intersect(new[] { tenant, role });

        result.Envelope.Network!.Allow.Should().Be(NetworkPolicy.Loopback);
        result.BlockedAxes.Should().ContainSingle(b => b.Axis == BlockedBy.Axes.Network && b.Code == BlockedBy.Codes.Narrowed);
    }

    [Fact]
    public void Network_AllowlistHosts_IntersectAcrossTiers()
    {
        var t1 = Tier("t1", network: new EnvelopeNetwork(NetworkPolicy.Allowlist, new[] { "a", "b", "c" }));
        var t2 = Tier("t2", network: new EnvelopeNetwork(NetworkPolicy.Allowlist, new[] { "b", "c", "d" }));

        var result = EnvelopeIntersection.Intersect(new[] { t1, t2 });

        result.Envelope.Network!.Allow.Should().Be(NetworkPolicy.Allowlist);
        result.Envelope.Network.AllowedHosts.Should().BeEquivalentTo(new[] { "b", "c" });
    }

    // ---- Budget ------------------------------------------------------------

    [Fact]
    public void Budget_TakesNumericMinAcrossTiers_FiresNarrowedDenial()
    {
        var t1 = Tier("t1", budget: new EnvelopeBudget(MaxTokens: 10_000, MaxRepairLoops: 5));
        var t2 = Tier("t2", budget: new EnvelopeBudget(MaxTokens: 4_000, MaxRepairLoops: 3));

        var result = EnvelopeIntersection.Intersect(new[] { t1, t2 });

        result.Envelope.Budget!.MaxTokens.Should().Be(4_000);
        result.Envelope.Budget.MaxRepairLoops.Should().Be(3);
        result.BlockedAxes.Where(b => b.Axis == BlockedBy.Axes.Budget).Should().HaveCount(2);
    }

    [Fact]
    public void Budget_NullField_OnOneTier_DoesNotNarrow()
    {
        var t1 = Tier("t1", budget: new EnvelopeBudget(MaxTokens: null, MaxRepairLoops: 5));
        var t2 = Tier("t2", budget: new EnvelopeBudget(MaxTokens: 4_000, MaxRepairLoops: null));

        var result = EnvelopeIntersection.Intersect(new[] { t1, t2 });

        result.Envelope.Budget!.MaxTokens.Should().Be(4_000);
        result.Envelope.Budget.MaxRepairLoops.Should().Be(5);
        result.BlockedAxes.Should().BeEmpty();
    }

    // ---- Workspace ---------------------------------------------------------

    [Fact]
    public void Workspace_SymlinkPolicy_TakesMostRestrictive()
    {
        var t1 = Tier("t1", workspace: new EnvelopeWorkspace(WorkspaceSymlinkPolicy.AllowAll));
        var t2 = Tier("t2", workspace: new EnvelopeWorkspace(WorkspaceSymlinkPolicy.RefuseForMutation));

        var result = EnvelopeIntersection.Intersect(new[] { t1, t2 });

        result.Envelope.Workspace!.SymlinkPolicy.Should().Be(WorkspaceSymlinkPolicy.RefuseForMutation);
    }

    [Fact]
    public void Workspace_CommandAllowlist_IntersectsAcrossTiers()
    {
        var t1 = Tier("t1", workspace: new EnvelopeWorkspace(
            WorkspaceSymlinkPolicy.AllowAll,
            CommandAllowlist: new[] { "git", "node", "npm" }));
        var t2 = Tier("t2", workspace: new EnvelopeWorkspace(
            WorkspaceSymlinkPolicy.AllowAll,
            CommandAllowlist: new[] { "git", "dotnet" }));

        var result = EnvelopeIntersection.Intersect(new[] { t1, t2 });

        result.Envelope.Workspace!.CommandAllowlist.Should().BeEquivalentTo(new[] { "git" });
    }

    [Fact]
    public void Workspace_AllowDirty_RequiresAllTiers()
    {
        var t1 = Tier("t1", workspace: new EnvelopeWorkspace(WorkspaceSymlinkPolicy.AllowAll, AllowDirty: true));
        var t2 = Tier("t2", workspace: new EnvelopeWorkspace(WorkspaceSymlinkPolicy.AllowAll, AllowDirty: false));

        var result = EnvelopeIntersection.Intersect(new[] { t1, t2 });

        result.Envelope.Workspace!.AllowDirty.Should().BeFalse();
    }

    // ---- Delivery ----------------------------------------------------------

    [Fact]
    public void Delivery_TiersAgree_PassesThrough()
    {
        var target = new DeliveryTarget("acme", "web", "main");
        var t1 = Tier("t1", delivery: target);
        var t2 = Tier("t2", delivery: target);

        var result = EnvelopeIntersection.Intersect(new[] { t1, t2 });

        result.Envelope.Delivery.Should().Be(target);
        result.BlockedAxes.Should().BeEmpty();
    }

    [Fact]
    public void Delivery_TiersConflict_BlocksAndReturnsNull()
    {
        var t1 = Tier("t1", delivery: new DeliveryTarget("acme", "web", "main"));
        var t2 = Tier("t2", delivery: new DeliveryTarget("acme", "web", "release"));

        var result = EnvelopeIntersection.Intersect(new[] { t1, t2 });

        result.Envelope.Delivery.Should().BeNull();
        result.BlockedAxes.Should().ContainSingle(b =>
            b.Axis == BlockedBy.Axes.Delivery && b.Code == BlockedBy.Codes.Conflict);
    }

    // ---- End-to-end --------------------------------------------------------

    [Fact]
    public void EndToEnd_TenantWorkflowRoleContext_Intersects_AllAxes()
    {
        // Realistic shape: tenant denies everything broad, workflow declares needs,
        // role grants tools, context narrows budget further. Every axis should land
        // at the most-restrictive value.
        var tenant = Tier(Tiers.Tenant,
            network: new EnvelopeNetwork(NetworkPolicy.Allowlist, new[] { "api.openai.com", "github.com" }),
            budget: new EnvelopeBudget(MaxTokens: 100_000),
            workspace: new EnvelopeWorkspace(WorkspaceSymlinkPolicy.RefuseForMutation));

        var workflow = Tier(Tiers.Workflow,
            repoScopes: new[]
            {
                new RepoScopeGrant("github.com/acme/web", "/", RepoAccess.Write)
            },
            delivery: new DeliveryTarget("acme", "web", "main"));

        var role = Tier(Tiers.Role,
            toolGrants: new[]
            {
                new ToolGrant("apply_patch", ToolGrant.CategoryHost),
                new ToolGrant("run_command", ToolGrant.CategoryHost)
            },
            workspace: new EnvelopeWorkspace(
                WorkspaceSymlinkPolicy.AllowAll,
                CommandAllowlist: new[] { "git", "node", "npm" }));

        var context = Tier(Tiers.Context,
            budget: new EnvelopeBudget(MaxTokens: 25_000));

        var result = EnvelopeIntersection.Intersect(new[] { tenant, workflow, role, context });

        result.Envelope.RepoScopes.Should().ContainSingle();
        result.Envelope.ToolGrants.Should().HaveCount(2);
        result.Envelope.Network!.AllowedHosts.Should().BeEquivalentTo(new[] { "api.openai.com", "github.com" });
        result.Envelope.Budget!.MaxTokens.Should().Be(25_000);
        result.Envelope.Workspace!.SymlinkPolicy.Should().Be(WorkspaceSymlinkPolicy.RefuseForMutation);
        result.Envelope.Workspace.CommandAllowlist.Should().BeEquivalentTo(new[] { "git", "node", "npm" });
        result.Envelope.Delivery.Should().Be(new DeliveryTarget("acme", "web", "main"));

        result.BlockedAxes.Should().ContainSingle(b =>
            b.Axis == BlockedBy.Axes.Budget && b.Tier == Tiers.Context && b.Code == BlockedBy.Codes.Narrowed);
    }

    // ---- Helpers -----------------------------------------------------------

    private static EnvelopeTier Tier(
        string name,
        IReadOnlyList<RepoScopeGrant>? repoScopes = null,
        IReadOnlyList<ToolGrant>? toolGrants = null,
        IReadOnlyList<ExecuteGrant>? executeGrants = null,
        EnvelopeNetwork? network = null,
        EnvelopeBudget? budget = null,
        EnvelopeWorkspace? workspace = null,
        DeliveryTarget? delivery = null)
    {
        return new EnvelopeTier(name, new WorkflowExecutionEnvelope(
            RepoScopes: repoScopes,
            ToolGrants: toolGrants,
            ExecuteGrants: executeGrants,
            Network: network,
            Budget: budget,
            Workspace: workspace,
            Delivery: delivery));
    }
}
