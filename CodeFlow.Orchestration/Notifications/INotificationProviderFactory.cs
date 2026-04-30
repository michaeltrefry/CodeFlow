using CodeFlow.Contracts.Notifications;
using CodeFlow.Persistence.Notifications;

namespace CodeFlow.Orchestration.Notifications;

/// <summary>
/// Builds <see cref="INotificationProvider"/> instances from a stored provider configuration.
/// One factory per channel family (Slack, Email, SMS, …); each factory turns N configuration
/// rows into N provider instances at runtime. Factories are registered as singletons; the
/// registry caches the resulting providers for the lifetime of its DI scope.
/// </summary>
public interface INotificationProviderFactory
{
    /// <summary>Channel family this factory handles. <see cref="INotificationProviderRegistry"/> dispatches by channel.</summary>
    NotificationChannel Channel { get; }

    /// <summary>
    /// Builds a provider instance for the given configuration row. Implementations may throw
    /// when the config is structurally invalid (e.g. missing credential for an auth-required
    /// channel) — the registry surfaces that as a "no provider available" return; the
    /// dispatcher then records a Failed audit row instead of attempting delivery.
    /// </summary>
    Task<INotificationProvider> CreateAsync(
        NotificationProviderConfigWithCredential config,
        CancellationToken cancellationToken = default);
}
