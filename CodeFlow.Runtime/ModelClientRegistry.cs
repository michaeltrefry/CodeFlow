namespace CodeFlow.Runtime;

public sealed class ModelClientRegistry
{
    private readonly IReadOnlyDictionary<string, Func<IModelClient>> factoriesByProvider;

    public ModelClientRegistry(IEnumerable<ModelClientRegistration> registrations)
        : this(ToFactories(registrations))
    {
    }

    /// <summary>
    /// Preferred ctor: factories are invoked per Resolve() call so each model invocation can
    /// pick up a fresh IHttpClientFactory-managed HttpClient (with its rotated underlying
    /// HttpMessageHandler). Capturing a single HttpClient instance for the process lifetime
    /// leaves provider DNS permanently stale.
    /// </summary>
    public ModelClientRegistry(IEnumerable<KeyValuePair<string, Func<IModelClient>>> factories)
    {
        ArgumentNullException.ThrowIfNull(factories);

        var byProvider = new Dictionary<string, Func<IModelClient>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (provider, factory) in factories)
        {
            if (string.IsNullOrWhiteSpace(provider))
            {
                throw new ArgumentException("Model client registrations require a provider key.", nameof(factories));
            }

            ArgumentNullException.ThrowIfNull(factory);

            if (!byProvider.TryAdd(provider, factory))
            {
                throw new InvalidOperationException($"Model provider '{provider}' is registered more than once.");
            }
        }

        factoriesByProvider = byProvider;
    }

    public IModelClient Resolve(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            throw new ArgumentException("A provider key is required.", nameof(provider));
        }

        if (!factoriesByProvider.TryGetValue(provider, out var factory))
        {
            throw new UnknownModelProviderException(provider);
        }

        return factory();
    }

    private static IEnumerable<KeyValuePair<string, Func<IModelClient>>> ToFactories(
        IEnumerable<ModelClientRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        foreach (var registration in registrations)
        {
            var captured = registration.Client;
            yield return new KeyValuePair<string, Func<IModelClient>>(registration.Provider, () => captured);
        }
    }
}
