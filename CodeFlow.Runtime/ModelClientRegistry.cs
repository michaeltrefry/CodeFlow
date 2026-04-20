namespace CodeFlow.Runtime;

public sealed class ModelClientRegistry
{
    private readonly IReadOnlyDictionary<string, IModelClient> clientsByProvider;

    public ModelClientRegistry(IEnumerable<ModelClientRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);

        var clients = new Dictionary<string, IModelClient>(StringComparer.OrdinalIgnoreCase);

        foreach (var registration in registrations)
        {
            if (string.IsNullOrWhiteSpace(registration.Provider))
            {
                throw new ArgumentException("Model client registrations require a provider key.", nameof(registrations));
            }

            if (!clients.TryAdd(registration.Provider, registration.Client))
            {
                throw new InvalidOperationException($"Model provider '{registration.Provider}' is registered more than once.");
            }
        }

        clientsByProvider = clients;
    }

    public IModelClient Resolve(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            throw new ArgumentException("A provider key is required.", nameof(provider));
        }

        if (!clientsByProvider.TryGetValue(provider, out var client))
        {
            throw new UnknownModelProviderException(provider);
        }

        return client;
    }
}
