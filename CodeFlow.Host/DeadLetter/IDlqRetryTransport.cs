namespace CodeFlow.Host.DeadLetter;

/// <summary>
/// Transfers a single dead-lettered message from an error queue to a target queue atomically —
/// the message is either on the source queue (untouched) or on the target queue (published and
/// confirmed), never in-flight or lost. Implementations rely on the broker's own delivery
/// mechanics (unacked state + channel-close requeue) to make this guarantee, rather than the
/// caller performing compensation on failure.
/// </summary>
public interface IDlqRetryTransport
{
    Task<DlqTransferResult> TransferAsync(
        string sourceQueue,
        string targetQueue,
        string targetMessageId,
        CancellationToken cancellationToken = default);
}

public sealed record DlqTransferResult(
    DlqTransferOutcome Outcome,
    int MessagesInspected,
    string? ErrorMessage);

public enum DlqTransferOutcome
{
    Transferred,
    NotFound,
    Error
}
