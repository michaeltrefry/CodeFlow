using CodeFlow.Contracts.Notifications;

namespace CodeFlow.Orchestration.Notifications;

/// <summary>
/// Resolves <see cref="INotificationProvider"/> instances by id at dispatch time. Routes
/// reference providers by string id (e.g. <c>slack-prod</c>); providers are registered in DI
/// (sc-54/55/56 register Slack/Email/SMS adapters). The dispatcher uses this registry rather
/// than an <c>IEnumerable&lt;INotificationProvider&gt;</c> dependency so it can detect a
/// missing/typo'd provider id and record a Failed audit row instead of silently dropping the
/// fan-out target.
/// </summary>
public interface INotificationProviderRegistry
{
    INotificationProvider? GetById(string providerId);
}
