using CodeFlow.Contracts.Notifications;
using CodeFlow.Persistence.Notifications;
using Microsoft.Extensions.Logging;

namespace CodeFlow.Orchestration.Notifications;

/// <summary>
/// Default registry. Static providers (DI-registered <see cref="INotificationProvider"/>
/// instances) win over factory-backed lookups so test fixtures and dev stubs can shadow a
/// real config without rebuilding the database. Factory lookups go through
/// <see cref="INotificationProviderConfigRepository.GetWithDecryptedCredentialAsync"/>;
/// archived or disabled rows are treated as absent. Resolved instances are cached per
/// registry instance (i.e. per scope — credential rotations land on the next saga consume).
/// </summary>
public sealed class NotificationProviderRegistry : INotificationProviderRegistry
{
    private readonly IReadOnlyDictionary<string, INotificationProvider> staticProviders;
    private readonly IReadOnlyDictionary<NotificationChannel, INotificationProviderFactory> factoriesByChannel;
    private readonly INotificationProviderConfigRepository? configRepository;
    private readonly ILogger<NotificationProviderRegistry>? logger;
    private readonly Dictionary<string, INotificationProvider?> resolvedCache = new(StringComparer.Ordinal);

    public NotificationProviderRegistry(
        IEnumerable<INotificationProvider> staticProviders,
        IEnumerable<INotificationProviderFactory> factories,
        INotificationProviderConfigRepository? configRepository = null,
        ILogger<NotificationProviderRegistry>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(staticProviders);
        ArgumentNullException.ThrowIfNull(factories);

        var byId = new Dictionary<string, INotificationProvider>(StringComparer.Ordinal);
        foreach (var provider in staticProviders)
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

        var byChannel = new Dictionary<NotificationChannel, INotificationProviderFactory>();
        foreach (var factory in factories)
        {
            ArgumentNullException.ThrowIfNull(factory);
            if (!byChannel.TryAdd(factory.Channel, factory))
            {
                throw new InvalidOperationException(
                    $"Multiple notification provider factories registered for channel {factory.Channel}. " +
                    "Each channel must have exactly one factory implementation.");
            }
        }

        this.staticProviders = byId;
        this.factoriesByChannel = byChannel;
        this.configRepository = configRepository;
        this.logger = logger;
    }

    public async ValueTask<INotificationProvider?> GetByIdAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        if (staticProviders.TryGetValue(providerId, out var staticProvider))
        {
            return staticProvider;
        }

        if (resolvedCache.TryGetValue(providerId, out var cached))
        {
            return cached;
        }

        if (configRepository is null)
        {
            // No DB-backed lookup wired (e.g. dispatcher tests using only static fakes).
            resolvedCache[providerId] = null;
            return null;
        }

        var config = await configRepository
            .GetWithDecryptedCredentialAsync(providerId, cancellationToken);

        if (config is null || !config.Config.Enabled || config.Config.IsArchived)
        {
            logger?.LogDebug(
                "No enabled provider configuration for id '{ProviderId}'; treating as missing.",
                providerId);
            resolvedCache[providerId] = null;
            return null;
        }

        if (!factoriesByChannel.TryGetValue(config.Config.Channel, out var factory))
        {
            logger?.LogWarning(
                "Provider configuration '{ProviderId}' references channel {Channel} but no factory is registered for that channel.",
                providerId, config.Config.Channel);
            resolvedCache[providerId] = null;
            return null;
        }

        try
        {
            var provider = await factory.CreateAsync(config, cancellationToken);
            resolvedCache[providerId] = provider;
            return provider;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex,
                "Factory for channel {Channel} threw while building provider '{ProviderId}'; treating as missing.",
                config.Config.Channel, providerId);
            resolvedCache[providerId] = null;
            return null;
        }
    }
}
