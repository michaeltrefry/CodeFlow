namespace CodeFlow.Runtime;

public sealed class UnknownModelProviderException(string provider)
    : InvalidOperationException($"Model provider '{provider}' is not registered.")
{
    public string Provider { get; } = provider;
}
