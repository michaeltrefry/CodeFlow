using CodeFlow.Contracts.Notifications;

namespace CodeFlow.Persistence.Notifications;

/// <summary>
/// Persisted configuration for a single notification provider instance (e.g. <c>slack-prod</c>,
/// <c>email-mailgun</c>, <c>sms-twilio</c>). One row per provider id; routes reference providers
/// by <see cref="Id"/>. Secrets live in <see cref="EncryptedCredential"/> protected by
/// <see cref="AesGcmSecretProtector"/> — they must never appear in API responses, audit rows,
/// or logs.
/// </summary>
public sealed class NotificationProviderEntity
{
    public string Id { get; set; } = null!;

    public string DisplayName { get; set; } = null!;

    public NotificationChannel Channel { get; set; }

    /// <summary>Non-secret transport endpoint (SMTP host, Slack workspace URL, SMS gateway base URL, …).</summary>
    public string? EndpointUrl { get; set; }

    /// <summary>Default sender shown to recipients (SMTP From, SMS sender id, Slack default channel).</summary>
    public string? FromAddress { get; set; }

    /// <summary>AES-GCM-encrypted credential blob — null when the provider does not require one (e.g. local SMTP relay).</summary>
    public byte[]? EncryptedCredential { get; set; }

    /// <summary>Provider-specific opaque settings as JSON (regional SMTP options, Slack scope flags, …). Optional.</summary>
    public string? AdditionalConfigJson { get; set; }

    public bool Enabled { get; set; }

    public bool IsArchived { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public string? CreatedBy { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public string? UpdatedBy { get; set; }
}
