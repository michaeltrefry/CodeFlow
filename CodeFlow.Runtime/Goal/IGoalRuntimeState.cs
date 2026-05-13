namespace CodeFlow.Runtime.Goal;

/// <summary>
/// Epic 978 — the runtime-state contract that <see cref="GoalHostToolProvider"/> reads from
/// when the agent calls <c>goal.get</c> and writes to when the agent calls
/// <c>goal.update(status="complete")</c>. The Goal-node executor (GN-3) implements this and
/// passes the instance into the agent invocation via
/// <see cref="AgentInvocationConfiguration.GoalState"/>; the presence of a non-null
/// implementation is what causes <see cref="Agent.BuildProviders"/> to surface the goal.* tools.
/// </summary>
/// <remarks>
/// Both reads and writes happen synchronously on the agent's invocation thread. The executor
/// loop inspects <see cref="Snapshot"/>.<see cref="GoalRuntimeStateSnapshot.IsCompleteRequested"/>
/// after each agent invocation returns to decide whether to exit via the <c>Success</c> port.
/// </remarks>
public interface IGoalRuntimeState
{
    /// <summary>
    /// Returns the current snapshot of the goal-run: objective text, token budget, tokens
    /// consumed so far, tokens remaining, and whether <see cref="MarkComplete"/> has been
    /// invoked by the agent in the current iteration. The snapshot is a value type; calling
    /// it twice in a row may return identical data or updated counters depending on whether
    /// the executor has rolled token accounting forward in between.
    /// </summary>
    GoalRuntimeStateSnapshot Snapshot();

    /// <summary>
    /// Signals that the agent has called <c>goal.update(status="complete")</c> for this
    /// iteration. The Goal-node executor checks this on the next loop tick and exits via the
    /// <c>Success</c> port if the audit prompt's requirements are otherwise satisfied. Idempotent
    /// — multiple calls in the same iteration are equivalent to one.
    /// </summary>
    void MarkComplete();

    /// <summary>
    /// Signals that the agent has called <c>goal.update(status="abandon")</c> because the
    /// objective is environmentally impossible (e.g. a tool consistently rejects every legitimate
    /// approach, an external dependency is unreachable, a prerequisite the workflow promised does
    /// not exist). The executor exits via the <c>Abandoned</c> port on the next loop tick so a
    /// downstream postmortem / HITL gate can investigate. Idempotent — the FIRST reason wins so a
    /// later call cannot rewrite an honest assessment; this is by design, not a bug.
    /// </summary>
    /// <param name="reason">Free-text rationale the agent must provide. The tool layer rejects
    /// empty / whitespace-only reasons before reaching here, so this is always a meaningful
    /// string. Plumbed into the Goal-node decision payload for downstream consumers.</param>
    void MarkAbandoned(string reason);
}

/// <summary>
/// Epic 978 — the snapshot data returned by <see cref="IGoalRuntimeState.Snapshot"/>. Mirrors
/// what <c>goal.get</c> shows the model so it can audit the budget without the executor having
/// to mediate every read.
/// </summary>
/// <param name="Objective">
/// The Scriban-rendered objective text for the current goal run. Authors set the template on
/// the Goal node (<c>goalObjective</c>); the executor renders it against <c>workflow.*</c>
/// once at goal-run entry and the result is what the agent sees throughout the loop.
/// </param>
/// <param name="TokenBudget">
/// The cumulative token cap configured on the Goal node, or <c>null</c> when the run is
/// unbounded. The system — not the model — enforces this cap.
/// </param>
/// <param name="TokensUsed">
/// Tokens consumed across every iteration of the current goal run so far (input + output,
/// summed). Sourced from the existing trace token-usage stream (epic 7ac46356).
/// </param>
/// <param name="TokensRemaining">
/// <see cref="TokenBudget"/> minus <see cref="TokensUsed"/>, clamped at zero. <c>null</c> when
/// the run is unbounded.
/// </param>
/// <param name="IsCompleteRequested">
/// <c>true</c> after the agent has called <c>goal.update(status="complete")</c> in the current
/// iteration; <c>false</c> otherwise. The executor's exit check, not a UI signal.
/// </param>
/// <param name="IsAbandonRequested">
/// <c>true</c> after the agent has called <c>goal.update(status="abandon")</c> in the current
/// iteration; <c>false</c> otherwise. Mutually exclusive with <see cref="IsCompleteRequested"/> in
/// practice (the executor exits as soon as either fires), but both are exposed independently so
/// the dispatcher can map cleanly to <c>Success</c> vs <c>Abandoned</c> ports without inferring.
/// </param>
/// <param name="AbandonReason">
/// The free-text rationale the agent passed to <c>goal.update(status="abandon", reason=...)</c>.
/// <c>null</c> when no abandon call has been made. Surfaces in the Goal-node decision payload and
/// the trace inspector so a postmortem / HITL gate can act on it without re-reading the
/// conversation history.
/// </param>
public sealed record GoalRuntimeStateSnapshot(
    string Objective,
    int? TokenBudget,
    int TokensUsed,
    int? TokensRemaining,
    bool IsCompleteRequested,
    bool IsAbandonRequested = false,
    string? AbandonReason = null);
