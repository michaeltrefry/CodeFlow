using CodeFlow.Runtime.Goal;
using FluentAssertions;

namespace CodeFlow.Runtime.Tests.Goal;

public sealed class MutableGoalRuntimeStateTests
{
    [Fact]
    public void Snapshot_FreshState_ZeroUsed_BudgetMinusZero_RemainingEqualsBudget()
    {
        var state = new MutableGoalRuntimeState("Ship it", tokenBudget: 100_000);

        var snapshot = state.Snapshot();

        snapshot.Objective.Should().Be("Ship it");
        snapshot.TokenBudget.Should().Be(100_000);
        snapshot.TokensUsed.Should().Be(0);
        snapshot.TokensRemaining.Should().Be(100_000);
        snapshot.IsCompleteRequested.Should().BeFalse();
    }

    [Fact]
    public void Snapshot_NullBudget_RemainingIsNull()
    {
        // Unbounded runs: the continuation template renders "unbounded" for both fields so the
        // agent never sees a misleading number.
        var state = new MutableGoalRuntimeState("explore", tokenBudget: null);

        state.AddTokensUsed(12_345);

        var snapshot = state.Snapshot();
        snapshot.TokenBudget.Should().BeNull();
        snapshot.TokensUsed.Should().Be(12_345);
        snapshot.TokensRemaining.Should().BeNull();
    }

    [Fact]
    public void Snapshot_AfterAdd_TokensUsedAdvancesAndRemainingClamps()
    {
        var state = new MutableGoalRuntimeState("ship", tokenBudget: 500);

        state.AddTokensUsed(200);
        state.AddTokensUsed(400);

        var snapshot = state.Snapshot();
        snapshot.TokensUsed.Should().Be(600);
        snapshot.TokensRemaining.Should().Be(0,
            "remaining clamps at zero so the continuation template never renders a negative number");
    }

    [Fact]
    public void MarkComplete_FlipsSnapshotFlag()
    {
        var state = new MutableGoalRuntimeState("o", tokenBudget: null);

        state.Snapshot().IsCompleteRequested.Should().BeFalse();
        state.MarkComplete();
        state.Snapshot().IsCompleteRequested.Should().BeTrue();
    }

    [Fact]
    public void MarkComplete_IsIdempotent()
    {
        var state = new MutableGoalRuntimeState("o", tokenBudget: null);

        state.MarkComplete();
        state.MarkComplete();
        state.MarkComplete();

        state.Snapshot().IsCompleteRequested.Should().BeTrue();
    }

    [Fact]
    public void ClearCompleteRequested_ResetsBetweenIterations()
    {
        var state = new MutableGoalRuntimeState("o", tokenBudget: null);
        state.MarkComplete();
        state.Snapshot().IsCompleteRequested.Should().BeTrue();

        state.ClearCompleteRequested();

        state.Snapshot().IsCompleteRequested.Should().BeFalse();
    }

    [Fact]
    public void IsBudgetExhausted_NullBudget_AlwaysFalse()
    {
        var state = new MutableGoalRuntimeState("o", tokenBudget: null);
        state.AddTokensUsed(int.MaxValue / 2);
        state.IsBudgetExhausted().Should().BeFalse();
    }

    [Fact]
    public void IsBudgetExhausted_TrueOnceUsedReachesBudget()
    {
        var state = new MutableGoalRuntimeState("o", tokenBudget: 100);
        state.IsBudgetExhausted().Should().BeFalse();
        state.AddTokensUsed(99);
        state.IsBudgetExhausted().Should().BeFalse();
        state.AddTokensUsed(1);
        state.IsBudgetExhausted().Should().BeTrue();
    }

    [Fact]
    public void IsBudgetExhausted_TrueOnOvershoot()
    {
        // Mid-iteration overruns are tolerated — the next-iteration gate catches them.
        var state = new MutableGoalRuntimeState("o", tokenBudget: 100);
        state.AddTokensUsed(250);
        state.IsBudgetExhausted().Should().BeTrue();
        state.Snapshot().TokensRemaining.Should().Be(0);
    }

    [Fact]
    public void Ctor_BlankObjective_Throws()
    {
        Assert.Throws<ArgumentException>(() => new MutableGoalRuntimeState("", tokenBudget: null));
        Assert.Throws<ArgumentException>(() => new MutableGoalRuntimeState("   ", tokenBudget: null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100000)]
    public void Ctor_NonPositiveBudget_Throws(int badBudget)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MutableGoalRuntimeState("o", tokenBudget: badBudget));
    }

    [Fact]
    public void AddTokensUsed_NegativeDelta_Throws()
    {
        var state = new MutableGoalRuntimeState("o", tokenBudget: null);
        Assert.Throws<ArgumentOutOfRangeException>(() => state.AddTokensUsed(-1));
    }
}
