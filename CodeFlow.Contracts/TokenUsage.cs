namespace CodeFlow.Contracts;

public sealed record TokenUsage(
    int InputTokens,
    int OutputTokens,
    int TotalTokens);
