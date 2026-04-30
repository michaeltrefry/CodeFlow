using CodeFlow.Contracts.Notifications;

namespace CodeFlow.Persistence.Notifications;

/// <summary>
/// Read-shape for a stored notification template snapshot. Rendering happens in sc-63;
/// sc-51 only owns persistence + version pinning.
/// </summary>
public sealed record NotificationTemplate(
    string TemplateId,
    int Version,
    NotificationEventKind EventKind,
    NotificationChannel Channel,
    string? SubjectTemplate,
    string BodyTemplate,
    DateTime CreatedAtUtc,
    string? CreatedBy,
    DateTime UpdatedAtUtc,
    string? UpdatedBy);

public sealed record NotificationTemplateUpsert(
    string TemplateId,
    NotificationEventKind EventKind,
    NotificationChannel Channel,
    string? SubjectTemplate,
    string BodyTemplate,
    string? UpdatedBy);
