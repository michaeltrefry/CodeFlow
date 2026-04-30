namespace CodeFlow.Contracts.Notifications;

/// <summary>
/// Outcome of a single provider delivery attempt. The dispatcher persists one of these per
/// (event, route, attempt) so audit + retry policy decisions in later slices have everything
/// they need without reaching back into provider state.
/// </summary>
/// <param name="EventId">Source event id.</param>
/// <param name="RouteId">Route the attempt fired on.</param>
/// <param name="ProviderId">Provider that handled the attempt.</param>
/// <param name="Status">Terminal status for this attempt.</param>
/// <param name="AttemptedAtUtc">UTC time the provider was invoked.</param>
/// <param name="CompletedAtUtc">UTC time the provider returned (null when in-flight is recorded ahead of completion).</param>
/// <param name="AttemptNumber">1-indexed retry counter.</param>
/// <param name="NormalizedDestination">Channel-appropriate destination string with secrets stripped (e.g. masked phone, channel id without token). Safe to log/display.</param>
/// <param name="ProviderMessageId">Provider-assigned id (Slack ts, SMTP id, Twilio sid, …) when delivery succeeded.</param>
/// <param name="ErrorCode">Stable provider error code when <see cref="Status"/> is failure-shaped.</param>
/// <param name="ErrorMessage">Human-readable error detail; must not contain credentials.</param>
public sealed record NotificationDeliveryResult(
    Guid EventId,
    string RouteId,
    string ProviderId,
    NotificationDeliveryStatus Status,
    DateTimeOffset AttemptedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    int AttemptNumber,
    string? NormalizedDestination = null,
    string? ProviderMessageId = null,
    string? ErrorCode = null,
    string? ErrorMessage = null);

/// <summary>Terminal outcome of a single delivery attempt.</summary>
public enum NotificationDeliveryStatus
{
    Unknown = 0,
    /// <summary>Provider accepted the message (e.g. SMTP 250, Slack ok=true, Twilio queued).</summary>
    Sent = 1,
    /// <summary>Provider rejected the message; eligible for retry per policy.</summary>
    Failed = 2,
    /// <summary>Route or message intentionally not sent (disabled route, severity below threshold, dedupe hit).</summary>
    Skipped = 3,
    /// <summary>Failure that the dispatcher will retry; recorded so audit shows the attempt.</summary>
    Retrying = 4,
    /// <summary>Operator/admin policy suppressed the delivery (do-not-disturb, block list, quiet hours).</summary>
    Suppressed = 5
}
