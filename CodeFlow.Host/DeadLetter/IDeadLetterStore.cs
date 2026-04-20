namespace CodeFlow.Host.DeadLetter;

public interface IDeadLetterStore
{
    Task<IReadOnlyList<DeadLetterQueueSummary>> ListQueuesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DeadLetterMessage>> ListMessagesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DeadLetterMessage>> PeekQueueAsync(string queueName, CancellationToken cancellationToken = default);

    Task<DeadLetterRetryResult> RetryAsync(string queueName, string messageId, CancellationToken cancellationToken = default);
}
