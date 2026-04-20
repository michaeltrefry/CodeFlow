namespace CodeFlow.Runtime;

public sealed record TokenUsage(
    int InputTokens,
    int OutputTokens,
    int TotalTokens);
