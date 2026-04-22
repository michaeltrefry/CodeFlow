namespace CodeFlow.Host.DeadLetter;

public sealed class DeadLetterOptions
{
    public const string SectionName = "DeadLetter";

    public string ManagementHost { get; set; } = "127.0.0.1";

    public int ManagementPort { get; set; } = 15672;

    public bool UseHttps { get; set; }

    public string VirtualHost { get; set; } = "codeflow";

    public string Username { get; set; } = "codeflow";

    public string Password { get; set; } = "codeflow_dev";

    public string ErrorQueueSuffix { get; set; } = "_error";

    public int MaxPeekPerQueue { get; set; } = 25;

    /// <summary>
    /// AMQP host for the native RabbitMQ client used by the atomic retry transport. Defaults to
    /// <see cref="ManagementHost"/> when left unset so single-host deployments don't need two
    /// entries.
    /// </summary>
    public string? AmqpHost { get; set; }

    /// <summary>
    /// AMQP port (5672 by default). The management HTTP API uses a different port
    /// (<see cref="ManagementPort"/>, typically 15672); retry uses native AMQP for atomicity.
    /// </summary>
    public int AmqpPort { get; set; } = 5672;

    /// <summary>
    /// Timeout for the "walk the DLQ looking for the target message" scan during atomic retry.
    /// Each <c>BasicGet</c> is a round-trip; the scan ends when the target is found, the queue
    /// empties, <see cref="MaxPeekPerQueue"/> messages have been inspected, or this timeout
    /// fires. Unexamined messages remain on the queue; inspected-but-not-matched messages are
    /// requeued automatically on channel close.
    /// </summary>
    public TimeSpan RetryScanTimeout { get; set; } = TimeSpan.FromSeconds(10);
}
