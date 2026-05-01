using CodeFlow.Contracts.Notifications;

namespace CodeFlow.Persistence.Notifications;

public interface INotificationDeliveryAttemptRepository
{
    /// <summary>
    /// Persists one attempt row. Translates the provider-shaped <see cref="NotificationDeliveryResult"/>
    /// into an entity row; throws when the row violates the unique
    /// (event_id, provider_id, normalized_destination, attempt_number) constraint, so callers
    /// must handle race conditions explicitly rather than silently swallowing them.
    /// </summary>
    Task RecordAsync(
        NotificationDeliveryResult result,
        NotificationEventKind eventKind,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NotificationDeliveryResult>> ListByEventIdAsync(
        Guid eventId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the most recent attempt (any status) for a given (event, provider, destination)
    /// triple. Used by the dispatcher's idempotency check before sending: if a Sent attempt
    /// already exists, skip; if the latest is Failed/Retrying, the next attempt_number is
    /// `result.AttemptNumber + 1`.
    /// </summary>
    Task<NotificationDeliveryResult?> LatestForDestinationAsync(
        Guid eventId,
        string providerId,
        string normalizedDestination,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Audit listing for the admin surface (sc-59). Returns rows in descending
    /// (<c>attempted_at_utc</c>, <c>id</c>) order so the most recent attempts surface first;
    /// <see cref="NotificationDeliveryAttemptListFilter.BeforeId"/> drives cursor pagination.
    /// </summary>
    Task<IReadOnlyList<NotificationDeliveryAttemptRecord>> ListAsync(
        NotificationDeliveryAttemptListFilter filter,
        CancellationToken cancellationToken = default);
}
