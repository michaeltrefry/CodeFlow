namespace CodeFlow.Runtime;

public sealed record RetryContext(
    int AttemptNumber,
    string? PriorFailureReason = null,
    string? PriorAttemptSummary = null);
