using CodeFlow.Contracts.Notifications;

namespace CodeFlow.Persistence.Notifications;

/// <summary>
/// Persisted routing rule mapping an event kind onto a provider + recipients + template. The
/// dispatcher (sc-52) loads enabled rows by <see cref="EventKind"/>, applies severity filtering,
/// renders the template, and fans out via the referenced <see cref="ProviderId"/>.
/// </summary>
public sealed class NotificationRouteEntity
{
    public string Id { get; set; } = null!;

    public NotificationEventKind EventKind { get; set; }

    public string ProviderId { get; set; } = null!;

    public string TemplateId { get; set; } = null!;

    public int TemplateVersion { get; set; }

    /// <summary>JSON-serialized <c>IReadOnlyList&lt;NotificationRecipient&gt;</c>.</summary>
    public string RecipientsJson { get; set; } = null!;

    public NotificationSeverity MinimumSeverity { get; set; }

    public bool Enabled { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}
