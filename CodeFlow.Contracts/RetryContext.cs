namespace CodeFlow.Contracts;

public sealed record RetryContext(
    int AttemptNumber,
    string? PriorFailureReason = null,
    string? PriorAttemptSummary = null);
