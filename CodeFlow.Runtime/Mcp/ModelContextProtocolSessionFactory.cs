using ModelContextProtocol.Client;

namespace CodeFlow.Runtime.Mcp;

public sealed class ModelContextProtocolSessionFactory : IMcpSessionFactory
{
    public async Task<IMcpSession> OpenAsync(McpServerConnectionInfo info, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(info);

        var transportOptions = new HttpClientTransportOptions
        {
            Endpoint = info.Endpoint,
            Name = info.Key,
            TransportMode = MapTransport(info.Transport),
        };

        if (!string.IsNullOrWhiteSpace(info.BearerToken))
        {
            transportOptions.AdditionalHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = $"Bearer {info.BearerToken}",
            };
        }

        var transport = new HttpClientTransport(transportOptions);
        var client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);

        return new ModelContextProtocolSession(info.Key, client);
    }

    private static HttpTransportMode MapTransport(McpTransportKind kind) =>
        kind switch
        {
            McpTransportKind.StreamableHttp => HttpTransportMode.StreamableHttp,
            McpTransportKind.HttpSse => HttpTransportMode.Sse,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported MCP transport."),
        };
}
