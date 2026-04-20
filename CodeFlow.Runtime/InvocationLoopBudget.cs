namespace CodeFlow.Runtime;

public sealed record InvocationLoopBudget
{
    public static InvocationLoopBudget Default { get; } = new();

    public int MaxToolCalls { get; init; } = 16;

    public TimeSpan MaxLoopDuration { get; init; } = TimeSpan.FromSeconds(60);

    public int MaxConsecutiveNonMutatingCalls { get; init; } = 8;
}
