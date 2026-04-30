using CodeFlow.Contracts.Notifications;

namespace CodeFlow.Orchestration.Notifications;

/// <summary>
/// Produces a fully-rendered <see cref="NotificationMessage"/> from a notification event +
/// route + recipient list. The dispatcher (sc-52) calls this once per route after the route
/// passes severity/dedupe checks; concrete providers (sc-54/55/56) consume the resulting
/// message verbatim. Implementations must preserve <see cref="INotificationEvent.ActionUrl"/>
/// on the rendered message — sc-50 + sc-62 require it.
/// </summary>
public interface INotificationTemplateRenderer
{
    Task<NotificationMessage> RenderAsync(
        INotificationEvent notificationEvent,
        NotificationRoute route,
        IReadOnlyList<NotificationRecipient> recipients,
        CancellationToken cancellationToken = default);
}
