using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace CodeFlow.Host.DeadLetter;

/// <summary>
/// Native AMQP retry path. The atomicity model:
/// <list type="number">
/// <item><description>
/// <c>BasicGet</c> with <c>autoAck=false</c> reserves a message on the channel — the broker still
/// owns it; it just won't redeliver to the same channel.
/// </description></item>
/// <item><description>
/// If the fetched message matches the target id, publish to the destination queue with publisher
/// confirms on. The publish is awaited — it returns only after the broker has persisted the new
/// copy. <c>BasicAck</c> then removes the original from the source queue.
/// </description></item>
/// <item><description>
/// Non-matching messages are left unacked; when the channel closes they requeue untouched.
/// </description></item>
/// </list>
/// If the process dies between the publish-confirmed and the ack, the broker still holds the
/// source copy in the unacked pool; on channel close (socket drop) it requeues. Net result: the
/// message appears on both queues (at-least-once semantics) rather than disappearing. Operators
/// can always re-run retry to clean up.
/// </summary>
public sealed class RabbitMqDlqRetryTransport : IDlqRetryTransport
{
    private readonly DeadLetterOptions options;
    private readonly ILogger<RabbitMqDlqRetryTransport> logger;

    public RabbitMqDlqRetryTransport(
        IOptions<DeadLetterOptions> options,
        ILogger<RabbitMqDlqRetryTransport> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        this.options = options.Value;
        this.logger = logger;
    }

    public async Task<DlqTransferResult> TransferAsync(
        string sourceQueue,
        string targetQueue,
        string targetMessageId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceQueue);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetQueue);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetMessageId);

        var factory = new ConnectionFactory
        {
            HostName = string.IsNullOrWhiteSpace(options.AmqpHost) ? options.ManagementHost : options.AmqpHost,
            Port = options.AmqpPort,
            VirtualHost = options.VirtualHost,
            UserName = options.Username,
            Password = options.Password,
            Ssl = options.UseHttps ? new SslOption { Enabled = true, ServerName = options.ManagementHost } : new SslOption()
        };

        await using var connection = await factory.CreateConnectionAsync(cancellationToken);
        await using var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(
                publisherConfirmationsEnabled: true,
                publisherConfirmationTrackingEnabled: true),
            cancellationToken);

        using var scanCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        scanCts.CancelAfter(options.RetryScanTimeout);

        var inspected = 0;
        var maxInspect = Math.Max(1, options.MaxPeekPerQueue);

        while (inspected < maxInspect)
        {
            scanCts.Token.ThrowIfCancellationRequested();

            BasicGetResult? result;
            try
            {
                result = await channel.BasicGetAsync(sourceQueue, autoAck: false, scanCts.Token);
            }
            catch (OperationCanceledException) when (scanCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    "DLQ retry scan timed out after {Elapsed} on {Queue} without finding message {MessageId} — inspected {Inspected} messages; all requeued on channel close",
                    options.RetryScanTimeout,
                    sourceQueue,
                    targetMessageId,
                    inspected);
                return new DlqTransferResult(
                    DlqTransferOutcome.NotFound,
                    inspected,
                    $"Scan timed out after {options.RetryScanTimeout} without finding the target message.");
            }

            if (result is null)
            {
                // Queue is empty — nothing to inspect. No messages were acked, so nothing moved.
                return new DlqTransferResult(DlqTransferOutcome.NotFound, inspected, null);
            }

            inspected++;
            var fetchedMessageId = result.BasicProperties.MessageId;

            if (!string.Equals(fetchedMessageId, targetMessageId, StringComparison.Ordinal))
            {
                // Not our target — leave it unacked. The broker won't redeliver to this channel;
                // when the channel closes (at method end), the unacked messages requeue untouched.
                continue;
            }

            // Target located. The publish-then-ack order is load-bearing: publisher confirms
            // must be satisfied before we release the original from the DLQ, so a publish failure
            // aborts here with the original still reservable by the next channel.
            var publishProperties = new BasicProperties(result.BasicProperties);

            await channel.BasicPublishAsync(
                exchange: string.Empty,
                routingKey: targetQueue,
                mandatory: true,
                basicProperties: publishProperties,
                body: result.Body,
                cancellationToken: cancellationToken);

            await channel.BasicAckAsync(
                deliveryTag: result.DeliveryTag,
                multiple: false,
                cancellationToken);

            return new DlqTransferResult(DlqTransferOutcome.Transferred, inspected, null);
        }

        return new DlqTransferResult(
            DlqTransferOutcome.NotFound,
            inspected,
            $"Target message id was not found within the first {maxInspect} messages on the queue.");
    }
}
