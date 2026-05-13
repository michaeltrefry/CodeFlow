namespace CodeFlow.Runtime.Goal;

/// <summary>
/// Epic 978 / GN-3 — the in-memory implementation of <see cref="IGoalRuntimeState"/> the
/// <see cref="GoalIterationOrchestrator"/> hands to each agent invocation. One instance per
/// goal run: created when the Goal node enters, updated by the orchestrator between
/// iterations (token accumulation), and inspected after each invocation to decide whether
/// to continue or exit via <c>Success</c>.
/// </summary>
/// <remarks>
/// The class is intentionally narrow: all state mutation goes through explicit setters the
/// orchestrator owns. The <c>goal.update</c> tool handler only flips
/// <see cref="GoalRuntimeStateSnapshot.IsCompleteRequested"/> via <see cref="MarkComplete"/>;
/// it cannot rewrite token counters or the objective from inside the agent.
/// </remarks>
public sealed class MutableGoalRuntimeState : IGoalRuntimeState
{
    private readonly Lock gate = new();
    private readonly string objective;
    private readonly int? tokenBudget;
    private int tokensUsed;
    private bool completeRequested;

    public MutableGoalRuntimeState(string objective, int? tokenBudget)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objective);
        if (tokenBudget is { } budget && budget <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(tokenBudget),
                budget,
                "Token budget, when set, must be positive. Pass null for an unbounded run.");
        }

        this.objective = objective;
        this.tokenBudget = tokenBudget;
    }

    public GoalRuntimeStateSnapshot Snapshot()
    {
        lock (gate)
        {
            var remaining = tokenBudget.HasValue
                ? Math.Max(0, tokenBudget.Value - tokensUsed)
                : (int?)null;
            return new GoalRuntimeStateSnapshot(
                Objective: objective,
                TokenBudget: tokenBudget,
                TokensUsed: tokensUsed,
                TokensRemaining: remaining,
                IsCompleteRequested: completeRequested);
        }
    }

    public void MarkComplete()
    {
        lock (gate)
        {
            completeRequested = true;
        }
    }

    /// <summary>
    /// Add tokens consumed by the most recent agent invocation. Called by
    /// <see cref="GoalIterationOrchestrator"/> after each <c>InvokeAsync</c> returns so the
    /// next iteration's <c>goal.get</c> + continuation prompt see fresh counters.
    /// </summary>
    public void AddTokensUsed(int delta)
    {
        if (delta < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(delta), delta, "Token usage deltas must be non-negative.");
        }

        lock (gate)
        {
            tokensUsed = checked(tokensUsed + delta);
        }
    }

    /// <summary>
    /// Reset the per-iteration completion signal between outer iterations. Called by the
    /// orchestrator before re-entering the loop so a stale <c>true</c> from a previous turn
    /// does not falsely exit. (No-op if MarkComplete was never called.)
    /// </summary>
    public void ClearCompleteRequested()
    {
        lock (gate)
        {
            completeRequested = false;
        }
    }

    /// <summary>
    /// True when the run has crossed its configured token budget. Returns <c>false</c> for
    /// unbounded runs regardless of consumption.
    /// </summary>
    public bool IsBudgetExhausted()
    {
        lock (gate)
        {
            return tokenBudget is { } b && tokensUsed >= b;
        }
    }
}
