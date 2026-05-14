using CodeFlow.Contracts;
using CodeFlow.Runtime;
using Xunit;

namespace CodeFlow.Orchestration.Tests;

public sealed class AgentOverrideMergeTests
{
    [Fact]
    public void MergeConfiguration_WithNullOverrides_ReturnsOriginal()
    {
        var config = new AgentInvocationConfiguration(
            Provider: "anthropic",
            Model: "claude-opus");

        var result = AgentOverrideMerge.MergeConfiguration(config, null);

        Assert.Same(config, result);
    }

    [Fact]
    public void MergeConfiguration_OverridesModel_AppliesOverride()
    {
        var config = new AgentInvocationConfiguration(
            Provider: "anthropic",
            Model: "claude-opus");
        var overrides = new AgentInvocationOverrides(
            ModelProvider: "anthropic",
            Model: "claude-sonnet");

        var result = AgentOverrideMerge.MergeConfiguration(config, overrides);

        Assert.Equal("anthropic", result.Provider);
        Assert.Equal("claude-sonnet", result.Model);
    }

    [Fact]
    public void MergeConfiguration_OverridesMaxTokens_AppliesOverride()
    {
        var config = new AgentInvocationConfiguration(
            Provider: "anthropic",
            Model: "claude-opus",
            MaxTokens: 1000);
        var overrides = new AgentInvocationOverrides(MaxOutputTokens: 2000);

        var result = AgentOverrideMerge.MergeConfiguration(config, overrides);

        Assert.Equal(2000, result.MaxTokens);
    }

    [Fact]
    public void MergeConfiguration_MaxTokensOverrideNull_InheritsAgent()
    {
        var config = new AgentInvocationConfiguration(
            Provider: "anthropic",
            Model: "claude-opus",
            MaxTokens: 1000);
        var overrides = new AgentInvocationOverrides(MaxOutputTokens: null);

        var result = AgentOverrideMerge.MergeConfiguration(config, overrides);

        Assert.Equal(1000, result.MaxTokens);
    }

    [Fact]
    public void MergeConfiguration_OverridesBudgetMaxToolCalls_AppliesOverride()
    {
        var budget = new InvocationLoopBudget { MaxToolCalls = 16 };
        var config = new AgentInvocationConfiguration(
            Provider: "anthropic",
            Model: "claude-opus",
            Budget: budget);
        var overrides = new AgentInvocationOverrides(MaxToolCalls: 24);

        var result = AgentOverrideMerge.MergeConfiguration(config, overrides);

        Assert.NotNull(result.Budget);
        Assert.Equal(24, result.Budget.MaxToolCalls);
    }

    [Fact]
    public void MergeConfiguration_OverridesBudgetDuration_AppliesOverride()
    {
        var budget = new InvocationLoopBudget { MaxLoopDuration = TimeSpan.FromMinutes(5) };
        var config = new AgentInvocationConfiguration(
            Provider: "anthropic",
            Model: "claude-opus",
            Budget: budget);
        var overrides = new AgentInvocationOverrides(MaxLoopDurationSeconds: 600); // 10 minutes

        var result = AgentOverrideMerge.MergeConfiguration(config, overrides);

        Assert.NotNull(result.Budget);
        Assert.Equal(TimeSpan.FromSeconds(600), result.Budget.MaxLoopDuration);
    }

    [Fact]
    public void MergeConfiguration_OverridesBudgetNonMutatingCalls_AppliesOverride()
    {
        var budget = new InvocationLoopBudget { MaxConsecutiveNonMutatingCalls = 8 };
        var config = new AgentInvocationConfiguration(
            Provider: "anthropic",
            Model: "claude-opus",
            Budget: budget);
        var overrides = new AgentInvocationOverrides(MaxConsecutiveNonMutatingCalls: 12);

        var result = AgentOverrideMerge.MergeConfiguration(config, overrides);

        Assert.NotNull(result.Budget);
        Assert.Equal(12, result.Budget.MaxConsecutiveNonMutatingCalls);
    }

    [Fact]
    public void MergeConfiguration_BudgetOverridesWithNullOriginalBudget_CreatesNewBudget()
    {
        var config = new AgentInvocationConfiguration(
            Provider: "anthropic",
            Model: "claude-opus",
            Budget: null);
        var overrides = new AgentInvocationOverrides(MaxToolCalls: 32);

        var result = AgentOverrideMerge.MergeConfiguration(config, overrides);

        Assert.NotNull(result.Budget);
        Assert.Equal(32, result.Budget.MaxToolCalls);
        // Other budget fields inherit from default.
        Assert.Equal(InvocationLoopBudget.Default.MaxLoopDuration, result.Budget.MaxLoopDuration);
    }

    [Fact]
    public void MergeConfiguration_NoBudgetOverrides_ReturnNullBudget()
    {
        var config = new AgentInvocationConfiguration(
            Provider: "anthropic",
            Model: "claude-opus",
            Budget: null);
        var overrides = new AgentInvocationOverrides(Model: "claude-sonnet");

        var result = AgentOverrideMerge.MergeConfiguration(config, overrides);

        Assert.Null(result.Budget);
    }

    [Fact]
    public void MergeConfiguration_PreservesOtherBudgetFields()
    {
        var budget = new InvocationLoopBudget
        {
            MaxToolCalls = 16,
            MaxLoopDuration = TimeSpan.FromMinutes(5),
            MaxConsecutiveNonMutatingCalls = 8,
            SoftWarnRemaining = 3,
            HardWarnRemaining = 1,
        };
        var config = new AgentInvocationConfiguration(
            Provider: "anthropic",
            Model: "claude-opus",
            Budget: budget);
        var overrides = new AgentInvocationOverrides(MaxToolCalls: 24);

        var result = AgentOverrideMerge.MergeConfiguration(config, overrides);

        Assert.NotNull(result.Budget);
        Assert.Equal(24, result.Budget.MaxToolCalls);
        // Non-overridden fields preserved.
        Assert.Equal(TimeSpan.FromMinutes(5), result.Budget.MaxLoopDuration);
        Assert.Equal(8, result.Budget.MaxConsecutiveNonMutatingCalls);
        Assert.Equal(3, result.Budget.SoftWarnRemaining);
        Assert.Equal(1, result.Budget.HardWarnRemaining);
    }

    [Fact]
    public void MergeConfiguration_MultipleBudgetOverrides_AppliesAll()
    {
        var budget = new InvocationLoopBudget
        {
            MaxToolCalls = 16,
            MaxLoopDuration = TimeSpan.FromMinutes(5),
            MaxConsecutiveNonMutatingCalls = 8,
        };
        var config = new AgentInvocationConfiguration(
            Provider: "anthropic",
            Model: "claude-opus",
            Budget: budget);
        var overrides = new AgentInvocationOverrides(
            MaxToolCalls: 32,
            MaxLoopDurationSeconds: 1200, // 20 minutes
            MaxConsecutiveNonMutatingCalls: 16);

        var result = AgentOverrideMerge.MergeConfiguration(config, overrides);

        Assert.NotNull(result.Budget);
        Assert.Equal(32, result.Budget.MaxToolCalls);
        Assert.Equal(TimeSpan.FromSeconds(1200), result.Budget.MaxLoopDuration);
        Assert.Equal(16, result.Budget.MaxConsecutiveNonMutatingCalls);
    }
}
