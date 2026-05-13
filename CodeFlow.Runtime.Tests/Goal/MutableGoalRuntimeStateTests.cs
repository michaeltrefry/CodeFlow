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

    [Fact]
    public void MarkAbandoned_RecordsReasonOnSnapshot()
    {
        var state = new MutableGoalRuntimeState("o", tokenBudget: null);

        state.Snapshot().IsAbandonRequested.Should().BeFalse();
        state.Snapshot().AbandonReason.Should().BeNull();

        state.MarkAbandoned("python is unreachable; container.run rejects every approach");

        var after = state.Snapshot();
        after.IsAbandonRequested.Should().BeTrue();
        after.AbandonReason.Should().Be("python is unreachable; container.run rejects every approach");
        after.IsCompleteRequested.Should().BeFalse(
            "abandon and complete are independent signals");
    }

    [Fact]
    public void MarkAbandoned_FirstReasonWins()
    {
        // Belt-and-brace against the model retracting an honest "this is impossible" with a
        // softer second call later in the same iteration. The orchestrator exits on the first
        // abandon anyway, but the state's invariant should survive even if the tool dispatcher
        // ever reordered calls.
        var state = new MutableGoalRuntimeState("o", tokenBudget: null);

        state.MarkAbandoned("first honest reason: env is broken");
        state.MarkAbandoned("second softer reason: actually nevermind");

        state.Snapshot().AbandonReason.Should().Be("first honest reason: env is broken");
    }

    [Fact]
    public void MarkAbandoned_BlankReason_Throws()
    {
        // Tool layer guards against this too, but the state itself enforces the invariant.
        // ArgumentException.ThrowIfNullOrWhiteSpace throws ArgumentNullException for null and
        // ArgumentException for empty/whitespace, so accept either subtype.
        var state = new MutableGoalRuntimeState("o", tokenBudget: null);

        Assert.Throws<ArgumentException>(() => state.MarkAbandoned(""));
        Assert.Throws<ArgumentException>(() => state.MarkAbandoned("   "));
        Assert.Throws<ArgumentNullException>(() => state.MarkAbandoned(null!));

        state.Snapshot().IsAbandonRequested.Should().BeFalse();
    }

    [Fact]
    public void ClearAbandonRequested_ResetsFlag_KeepsReason()
    {
        // The flag clears so a stale iteration doesn't falsely exit, but the reason is
        // preserved as the source of truth in case the executor somehow loops again after a
        // real abandon already fired.
        var state = new MutableGoalRuntimeState("o", tokenBudget: null);
        state.MarkAbandoned("blocker X");
        state.Snapshot().IsAbandonRequested.Should().BeTrue();

        state.ClearAbandonRequested();

        var after = state.Snapshot();
        after.IsAbandonRequested.Should().BeFalse();
        after.AbandonReason.Should().Be("blocker X",
            "reason survives the flag clear by design — it remains the source of truth");
    }

    [Fact]
    public void MarkAbandoned_DoesNotAffectCompleteFlag()
    {
        var state = new MutableGoalRuntimeState("o", tokenBudget: null);
        state.MarkAbandoned("reason");
        state.Snapshot().IsCompleteRequested.Should().BeFalse();
    }

    [Fact]
    public void MarkComplete_DoesNotAffectAbandonFlag()
    {
        var state = new MutableGoalRuntimeState("o", tokenBudget: null);
        state.MarkComplete();
        var snap = state.Snapshot();
        snap.IsAbandonRequested.Should().BeFalse();
        snap.AbandonReason.Should().BeNull();
    }
}
