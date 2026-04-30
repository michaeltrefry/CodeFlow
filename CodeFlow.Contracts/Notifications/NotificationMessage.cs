namespace CodeFlow.Contracts.Notifications;

/// <summary>
/// A rendered, ready-to-send message produced by the dispatcher and handed to a provider.
/// Subject is optional because SMS-shaped channels do not carry one; <see cref="Body"/> and
/// <see cref="ActionUrl"/> are required so every delivery includes both readable copy and the
/// canonical handling link.
/// </summary>
/// <param name="EventId">Source <see cref="INotificationEvent.EventId"/>; carried through for dedupe + audit.</param>
/// <param name="EventKind">Source <see cref="INotificationEvent.Kind"/>.</param>
/// <param name="Channel">Transport family the message targets.</param>
/// <param name="Recipients">One or more recipients — fan-out per recipient is the dispatcher's responsibility.</param>
/// <param name="Subject">Channel-appropriate subject line; may be null for channels without subjects (SMS).</param>
/// <param name="Body">Rendered message body — must already be channel-safe (no unrendered template syntax, no secrets).</param>
/// <param name="ActionUrl">Canonical CodeFlow deep-link the body must reference; preserved verbatim from the event for audit.</param>
/// <param name="Template">Pointer to the template that produced this message (null only for ad-hoc test sends).</param>
/// <param name="Severity">Severity inherited from the source event.</param>
public sealed record NotificationMessage(
    Guid EventId,
    NotificationEventKind EventKind,
    NotificationChannel Channel,
    IReadOnlyList<NotificationRecipient> Recipients,
    string Body,
    Uri ActionUrl,
    NotificationSeverity Severity,
    string? Subject = null,
    NotificationTemplateRef? Template = null);
