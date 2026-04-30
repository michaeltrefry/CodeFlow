using CodeFlow.Contracts.Notifications;

namespace CodeFlow.Orchestration.Notifications;

/// <summary>
/// Thrown when a route references a template version that does not exist in the template
/// store. The dispatcher converts this into a <see cref="NotificationDeliveryStatus.Failed"/>
/// audit row rather than propagating — a missing template must not crash fan-out for other
/// routes attached to the same event.
/// </summary>
public sealed class NotificationTemplateNotFoundException : Exception
{
    public NotificationTemplateNotFoundException(NotificationTemplateRef templateRef)
        : base($"Notification template '{templateRef.TemplateId}' version {templateRef.Version} was not found.")
    {
        TemplateRef = templateRef;
    }

    public NotificationTemplateRef TemplateRef { get; }
}
