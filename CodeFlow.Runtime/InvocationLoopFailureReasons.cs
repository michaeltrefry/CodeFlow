namespace CodeFlow.Runtime;

public static class InvocationLoopFailureReasons
{
    public const string ToolCallBudgetExceeded = "ToolCallBudgetExceeded";
    public const string LoopDurationExceeded = "LoopDurationExceeded";
    public const string ConsecutiveNonMutatingCallsExceeded = "ConsecutiveNonMutatingCallsExceeded";
}
