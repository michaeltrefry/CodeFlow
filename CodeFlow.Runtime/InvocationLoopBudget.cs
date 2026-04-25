namespace CodeFlow.Runtime;

public sealed record InvocationLoopBudget
{
    public static InvocationLoopBudget Default { get; } = new();

    public int MaxToolCalls { get; init; } = 16;

    /// <summary>
    /// Hard ceiling on wall-clock duration of a single agent invocation. Guards against runaway
    /// loops, but generous enough to accommodate genuinely long generations (a full PRD, a
    /// multi-page review, a large refactor). 5 minutes is the practical floor for a single
    /// non-streamed LLM call against current frontier models. Authors who want a tighter cap can
    /// override per-agent via <see cref="AgentInvocationConfiguration.Budget"/>.
    /// </summary>
    public TimeSpan MaxLoopDuration { get; init; } = TimeSpan.FromMinutes(5);

    public int MaxConsecutiveNonMutatingCalls { get; init; } = 8;
}
