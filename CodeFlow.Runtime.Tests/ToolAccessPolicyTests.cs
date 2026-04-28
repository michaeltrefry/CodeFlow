using FluentAssertions;

namespace CodeFlow.Runtime.Tests;

public sealed class ToolAccessPolicyTests
{
    [Fact]
    public void AllowAll_AllowsAnyTool()
    {
        ToolAccessPolicy.AllowAll.AllowsTool("anything").Should().BeTrue();
        ToolAccessPolicy.AllowAll.AllowsTool("get_workflow").Should().BeTrue();
    }

    [Fact]
    public void NoTools_DeniesEveryTool()
    {
        ToolAccessPolicy.NoTools.AllowsTool("anything").Should().BeFalse();
        ToolAccessPolicy.NoTools.AllowsTool("get_workflow").Should().BeFalse();
        ToolAccessPolicy.NoTools.DenyAll.Should().BeTrue();
    }

    [Fact]
    public void AllowedToolNames_FiltersByMembership_WhenNotDenyAll()
    {
        var policy = new ToolAccessPolicy(AllowedToolNames: new[] { "alpha", "beta" });

        policy.AllowsTool("alpha").Should().BeTrue();
        policy.AllowsTool("BETA").Should().BeTrue(because: "match is case-insensitive");
        policy.AllowsTool("gamma").Should().BeFalse();
    }

    [Fact]
    public void DenyAll_OverridesAllowedToolNames()
    {
        // Defense-in-depth: if a caller mistakenly populates an allowlist on a deny-all policy,
        // deny-all wins and nothing leaks through.
        var policy = new ToolAccessPolicy(
            AllowedToolNames: new[] { "alpha" },
            DenyAll: true);

        policy.AllowsTool("alpha").Should().BeFalse();
    }
}
