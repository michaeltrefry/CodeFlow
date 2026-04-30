namespace CodeFlow.Contracts.Notifications;

/// <summary>
/// Provider-neutral destination address for a notification. <see cref="Address"/> is the raw
/// channel-appropriate target ("user@example.com", "+15551234567", "C012AB3CD", …); the
/// dispatcher and provider validate format. Do not store secrets here — provider credentials
/// belong in protected configuration referenced by <see cref="NotificationRoute.ProviderId"/>.
/// </summary>
/// <param name="Channel">Transport family this recipient belongs to.</param>
/// <param name="Address">Channel-specific address (email, phone, channel id, …).</param>
/// <param name="DisplayName">Optional human-readable label for UI/audit surfaces.</param>
public sealed record NotificationRecipient(
    NotificationChannel Channel,
    string Address,
    string? DisplayName = null);
