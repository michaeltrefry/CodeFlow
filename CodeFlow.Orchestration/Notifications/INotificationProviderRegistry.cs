using CodeFlow.Contracts.Notifications;

namespace CodeFlow.Orchestration.Notifications;

/// <summary>
/// Resolves <see cref="INotificationProvider"/> instances by id at dispatch time. Routes
/// reference providers by string id (e.g. <c>slack-prod</c>); concrete provider instances are
/// either statically registered in DI (test/dev fakes) or built on demand by an
/// <see cref="INotificationProviderFactory"/> from a stored configuration row (sc-51). The
/// dispatcher uses this registry rather than an <c>IEnumerable&lt;INotificationProvider&gt;</c>
/// dependency so it can detect a missing/typo'd provider id and record a Failed audit row
/// instead of silently dropping the fan-out target.
/// </summary>
public interface INotificationProviderRegistry
{
    ValueTask<INotificationProvider?> GetByIdAsync(string providerId, CancellationToken cancellationToken = default);
}
