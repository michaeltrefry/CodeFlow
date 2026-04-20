namespace CodeFlow.Api.Dtos;

public sealed record DeadLetterQueueDto(string QueueName, int MessageCount);

public sealed record DeadLetterMessageDto(
    string MessageId,
    string QueueName,
    string? OriginalInputAddress,
    string? FaultExceptionMessage,
    string? FaultExceptionType,
    DateTimeOffset? FirstFaultedAtUtc,
    string PayloadPreview);

public sealed record DeadLetterListResponse(
    IReadOnlyList<DeadLetterQueueDto> Queues,
    IReadOnlyList<DeadLetterMessageDto> Messages);

public sealed record DeadLetterRetryResponse(
    bool Success,
    string? RepublishedTo,
    string? ErrorMessage);
