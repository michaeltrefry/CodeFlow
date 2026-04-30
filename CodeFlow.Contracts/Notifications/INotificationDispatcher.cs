namespace CodeFlow.Contracts.Notifications;

/// <summary>
/// Saga-facing entry point for the notification subsystem. Orchestration code (sc-53) calls
/// <see cref="DispatchAsync"/> with a provider-neutral event; the dispatcher (sc-52) resolves
/// routes, renders templates, fans out to providers, isolates failures, and persists audit
/// records. HITL task creation must not block on this call — implementations are expected to
/// hand off asynchronously and never throw transport exceptions back to the caller.
/// </summary>
public interface INotificationDispatcher
{
    /// <summary>
    /// Hands an event to the notification subsystem. Returns one
    /// <see cref="NotificationDeliveryResult"/> per route/recipient pair attempted; an empty
    /// list means no routes matched (which is a successful "nothing to do", not an error).
    /// </summary>
    Task<IReadOnlyList<NotificationDeliveryResult>> DispatchAsync(
        INotificationEvent notificationEvent,
        CancellationToken cancellationToken = default);
}
