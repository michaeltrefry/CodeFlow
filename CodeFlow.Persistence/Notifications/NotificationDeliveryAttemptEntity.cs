using CodeFlow.Contracts.Notifications;

namespace CodeFlow.Persistence.Notifications;

/// <summary>
/// One row per provider delivery attempt. Backs the audit/troubleshooting surface (sc-59) and
/// gives the dispatcher (sc-52) the data needed to enforce idempotency: before sending, the
/// dispatcher checks for an existing <c>Sent</c> row at the same (event, provider, destination)
/// triple and skips if one exists. The unique index on
/// (event_id, provider_id, normalized_destination, attempt_number) prevents duplicate audit
/// rows even under retry races.
/// </summary>
public sealed class NotificationDeliveryAttemptEntity
{
    public long Id { get; set; }

    public Guid EventId { get; set; }

    public NotificationEventKind EventKind { get; set; }

    public string RouteId { get; set; } = null!;

    public string ProviderId { get; set; } = null!;

    public NotificationDeliveryStatus Status { get; set; }

    public int AttemptNumber { get; set; }

    public DateTime AttemptedAtUtc { get; set; }

    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>
    /// Channel-appropriate destination string with secrets stripped. Required for the dedupe
    /// triple — providers must populate even for fan-out targets that share a transport.
    /// </summary>
    public string NormalizedDestination { get; set; } = null!;

    public string? ProviderMessageId { get; set; }

    public string? ErrorCode { get; set; }

    public string? ErrorMessage { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
