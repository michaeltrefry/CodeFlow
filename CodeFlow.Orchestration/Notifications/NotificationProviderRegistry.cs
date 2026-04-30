using CodeFlow.Contracts.Notifications;

namespace CodeFlow.Orchestration.Notifications;

public sealed class NotificationProviderRegistry : INotificationProviderRegistry
{
    private readonly IReadOnlyDictionary<string, INotificationProvider> providersById;

    public NotificationProviderRegistry(IEnumerable<INotificationProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        var byId = new Dictionary<string, INotificationProvider>(StringComparer.Ordinal);
        foreach (var provider in providers)
        {
            ArgumentNullException.ThrowIfNull(provider);
            ArgumentException.ThrowIfNullOrWhiteSpace(provider.Id);

            if (!byId.TryAdd(provider.Id, provider))
            {
                throw new InvalidOperationException(
                    $"Multiple notification providers registered with id '{provider.Id}'. " +
                    "Provider ids must be unique across DI registrations.");
            }
        }

        providersById = byId;
    }

    public INotificationProvider? GetById(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        return providersById.TryGetValue(providerId, out var provider) ? provider : null;
    }
}
