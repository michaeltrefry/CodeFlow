using CodeFlow.Contracts.Notifications;

namespace CodeFlow.Persistence.Notifications;

/// <summary>
/// Versioned template snapshot. The (TemplateId, Version) composite key gives audit-stable
/// references — once a delivery attempt records a template version, that exact bytes-on-disk
/// content is preserved even after admins publish a new version. Rendering lives in sc-63;
/// sc-51 just owns storage.
/// </summary>
public sealed class NotificationTemplateEntity
{
    public string TemplateId { get; set; } = null!;

    public int Version { get; set; }

    public NotificationEventKind EventKind { get; set; }

    public NotificationChannel Channel { get; set; }

    /// <summary>Subject template (Scriban or compatible). Null for SMS-shaped channels with no subject.</summary>
    public string? SubjectTemplate { get; set; }

    /// <summary>Body template (Scriban or compatible). Required.</summary>
    public string BodyTemplate { get; set; } = null!;

    public DateTime CreatedAtUtc { get; set; }

    public string? CreatedBy { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public string? UpdatedBy { get; set; }
}
