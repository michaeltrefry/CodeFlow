namespace CodeFlow.Host;

public sealed record RabbitMqTransportOptions
{
    public string Host { get; init; } = "127.0.0.1";

    public int Port { get; init; } = 5673;

    public string VirtualHost { get; init; } = "codeflow";

    public string Username { get; init; } = "codeflow";

    public string Password { get; init; } = "codeflow_dev";

    public ushort? PrefetchCount { get; init; }

    public int? ConsumerConcurrencyLimit { get; init; }
}
