namespace CodeFlow.Host.DeadLetter;

public sealed record DeadLetterQueueSummary(
    string QueueName,
    int MessageCount);

public sealed record DeadLetterMessage(
    string MessageId,
    string QueueName,
    string? OriginalInputAddress,
    string? FaultExceptionMessage,
    string? FaultExceptionType,
    DateTimeOffset? FirstFaultedAtUtc,
    string PayloadPreview,
    IReadOnlyDictionary<string, string> Headers);

public sealed record DeadLetterRetryResult(
    bool Success,
    string? RepublishedTo,
    string? ErrorMessage);
