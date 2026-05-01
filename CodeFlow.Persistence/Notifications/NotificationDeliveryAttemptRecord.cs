using CodeFlow.Contracts.Notifications;

namespace CodeFlow.Persistence.Notifications;

/// <summary>
/// Audit projection of <see cref="NotificationDeliveryAttemptEntity"/>. Carries the full row
/// the admin audit surface (sc-59) needs — including <see cref="EventKind"/>, <see cref="Id"/>,
/// and <see cref="CreatedAtUtc"/> — without leaking the EF entity to the API layer. The
/// existing dispatcher hot-path returns the contract <c>NotificationDeliveryResult</c>; this
/// record is for read-only audit queries.
/// </summary>
public sealed record NotificationDeliveryAttemptRecord(
    long Id,
    Guid EventId,
    NotificationEventKind EventKind,
    string RouteId,
    string ProviderId,
    NotificationDeliveryStatus Status,
    int AttemptNumber,
    DateTimeOffset AttemptedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string NormalizedDestination,
    string? ProviderMessageId,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// Filter set for the audit listing endpoint. Cursor pagination uses <see cref="BeforeId"/>:
/// callers pass the smallest <c>id</c> they've already seen and get the next page in
/// descending order. <see cref="Limit"/> caps how many rows the repository returns; the API
/// layer clamps user input before passing it down.
/// </summary>
public sealed record NotificationDeliveryAttemptListFilter(
    Guid? EventId = null,
    string? ProviderId = null,
    string? RouteId = null,
    NotificationDeliveryStatus? Status = null,
    DateTimeOffset? SinceUtc = null,
    long? BeforeId = null,
    int Limit = 50);
