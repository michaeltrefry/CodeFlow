namespace CodeFlow.Contracts.Notifications;

/// <summary>
/// Routing rule that maps a <see cref="NotificationEventKind"/> onto a concrete provider +
/// recipient set + template. Persistence lives in sc-51; this record is the in-memory shape
/// the dispatcher consumes after configuration has been resolved.
/// </summary>
/// <param name="RouteId">Stable identifier for the route (used in audit + UI).</param>
/// <param name="EventKind">Event family this route fires on.</param>
/// <param name="ProviderId">Provider instance id (e.g. <c>slack-prod</c>); resolves to an <see cref="INotificationProvider"/>.</param>
/// <param name="Recipients">Recipients the provider should target.</param>
/// <param name="Template">Template used to render the outbound <see cref="NotificationMessage"/>.</param>
/// <param name="MinimumSeverity">Suppresses the route when the event severity is below this value.</param>
/// <param name="Enabled">Disable without deleting (admin pause, broken provider creds, …).</param>
public sealed record NotificationRoute(
    string RouteId,
    NotificationEventKind EventKind,
    string ProviderId,
    IReadOnlyList<NotificationRecipient> Recipients,
    NotificationTemplateRef Template,
    NotificationSeverity MinimumSeverity = NotificationSeverity.Info,
    bool Enabled = true);
