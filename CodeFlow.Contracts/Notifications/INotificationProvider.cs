namespace CodeFlow.Contracts.Notifications;

/// <summary>
/// Provider-side contract for a single configured destination type (Slack workspace, SMTP
/// relay, SMS gateway, …). Implementations live alongside their transport SDK in later
/// slices; sc-50 only defines the surface so the dispatcher (sc-52) and adapters
/// (sc-54/55/56) can be wired without rewrites.
/// </summary>
public interface INotificationProvider
{
    /// <summary>
    /// Stable identifier for this provider instance (e.g. <c>slack-prod</c>, <c>email-mailgun</c>).
    /// Routes reference providers by id, not by .NET type, so multiple instances of the same
    /// channel can coexist (per-team Slack, per-region SMTP, …).
    /// </summary>
    string Id { get; }

    /// <summary>Transport family this provider speaks.</summary>
    NotificationChannel Channel { get; }

    /// <summary>
    /// Sends a single rendered message and returns a terminal <see cref="NotificationDeliveryResult"/>.
    /// Implementations must not throw on transport failures — surface them as
    /// <see cref="NotificationDeliveryStatus.Failed"/> so the dispatcher's retry/audit pipeline
    /// can record them without per-provider catch logic.
    /// </summary>
    Task<NotificationDeliveryResult> SendAsync(
        NotificationMessage message,
        NotificationRoute route,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates the provider's current configuration without sending a real message. Used by
    /// the admin "test connection" path (sc-58) and by config save validation (sc-57).
    /// </summary>
    Task<ProviderValidationResult> ValidateAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Outcome of <see cref="INotificationProvider.ValidateAsync"/>. <see cref="IsValid"/> false
/// surfaces both configuration errors (missing creds) and transport reachability failures —
/// the UI can render <see cref="ErrorMessage"/> verbatim.
/// </summary>
/// <param name="IsValid">True only when the provider is ready to deliver.</param>
/// <param name="ErrorCode">Stable code suitable for telemetry/UI branching when invalid.</param>
/// <param name="ErrorMessage">Human-readable detail; must not contain credentials.</param>
public sealed record ProviderValidationResult(
    bool IsValid,
    string? ErrorCode = null,
    string? ErrorMessage = null)
{
    public static ProviderValidationResult Valid() => new(true);

    public static ProviderValidationResult Invalid(string errorCode, string errorMessage) =>
        new(false, errorCode, errorMessage);
}
