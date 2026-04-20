namespace CodeFlow.Runtime;

public enum InvocationStopReason
{
    Unknown,
    EndTurn,
    ToolCalls,
    MaxTokens,
    StopSequence,
    ContentFilter
}
