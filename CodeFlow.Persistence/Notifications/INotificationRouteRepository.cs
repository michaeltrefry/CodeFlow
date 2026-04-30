using CodeFlow.Contracts.Notifications;

namespace CodeFlow.Persistence.Notifications;

public interface INotificationRouteRepository
{
    Task<IReadOnlyList<NotificationRoute>> ListAsync(CancellationToken cancellationToken = default);

    Task<NotificationRoute?> GetAsync(string routeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Enabled routes only, ordered by id for deterministic dispatcher fan-out. Used by the
    /// dispatcher (sc-52) on every event.
    /// </summary>
    Task<IReadOnlyList<NotificationRoute>> ListByEventKindAsync(
        NotificationEventKind eventKind,
        CancellationToken cancellationToken = default);

    Task UpsertAsync(NotificationRoute route, CancellationToken cancellationToken = default);

    Task DeleteAsync(string routeId, CancellationToken cancellationToken = default);
}
