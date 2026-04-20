using System.Text.Json.Nodes;
using CodeFlow.Runtime;
using CodeFlow.Runtime.Mcp;
using FluentAssertions;

namespace CodeFlow.Runtime.Tests.Mcp;

public sealed class DefaultMcpClientTests
{
    private static readonly McpServerConnectionInfo ArtifactsServer = new(
        Key: "artifacts",
        Endpoint: new Uri("https://artifacts.local/mcp"),
        Transport: McpTransportKind.StreamableHttp,
        BearerToken: "token-a");

    private static readonly McpServerConnectionInfo SearchServer = new(
        Key: "search",
        Endpoint: new Uri("https://search.local/mcp"),
        Transport: McpTransportKind.HttpSse,
        BearerToken: null);

    [Fact]
    public async Task InvokeAsync_opens_session_from_factory_and_forwards_call()
    {
        var provider = new FakeInfoProvider(ArtifactsServer);
        var factory = new FakeSessionFactory();

        await using var client = new DefaultMcpClient(provider, factory);

        var result = await client.InvokeAsync(
            server: "artifacts",
            toolName: "read",
            arguments: new JsonObject { ["uri"] = "file://a" });

        result.Content.Should().Be("artifacts:read");
        result.IsError.Should().BeFalse();

        factory.OpenCount.Should().Be(1);
        factory.OpenedInfos.Should().ContainSingle().Which.Should().Be(ArtifactsServer);
        factory.Sessions[0].CallLog.Should().ContainSingle()
            .Which.Should().Be(("read", "{\"uri\":\"file://a\"}"));
    }

    [Fact]
    public async Task InvokeAsync_caches_session_per_server_key_across_calls()
    {
        var provider = new FakeInfoProvider(ArtifactsServer);
        var factory = new FakeSessionFactory();

        await using var client = new DefaultMcpClient(provider, factory);

        await client.InvokeAsync("artifacts", "read", null);
        await client.InvokeAsync("artifacts", "write", null);
        await client.InvokeAsync("artifacts", "read", null);

        factory.OpenCount.Should().Be(1);
        factory.Sessions[0].CallLog.Should().HaveCount(3);
    }

    [Fact]
    public async Task InvokeAsync_opens_separate_sessions_for_different_server_keys()
    {
        var provider = new FakeInfoProvider(ArtifactsServer, SearchServer);
        var factory = new FakeSessionFactory();

        await using var client = new DefaultMcpClient(provider, factory);

        await client.InvokeAsync("artifacts", "read", null);
        await client.InvokeAsync("search", "query", null);
        await client.InvokeAsync("artifacts", "write", null);

        factory.OpenCount.Should().Be(2);
        factory.OpenedInfos.Should().BeEquivalentTo(new[] { ArtifactsServer, SearchServer });
    }

    [Fact]
    public async Task InvokeAsync_throws_when_provider_does_not_recognize_server_key()
    {
        var provider = new FakeInfoProvider();
        var factory = new FakeSessionFactory();

        await using var client = new DefaultMcpClient(provider, factory);

        var act = () => client.InvokeAsync("unknown", "anything", null);

        await act.Should().ThrowAsync<McpServerNotConfiguredException>()
            .Where(x => x.ServerKey == "unknown");
    }

    [Fact]
    public async Task DisposeAsync_disposes_every_open_session()
    {
        var provider = new FakeInfoProvider(ArtifactsServer, SearchServer);
        var factory = new FakeSessionFactory();

        var client = new DefaultMcpClient(provider, factory);
        await client.InvokeAsync("artifacts", "read", null);
        await client.InvokeAsync("search", "query", null);

        await client.DisposeAsync();

        factory.Sessions.Should().HaveCount(2);
        factory.Sessions.Should().OnlyContain(s => s.Disposed);
    }

    [Fact]
    public async Task InvokeAsync_rejects_calls_after_disposal()
    {
        var provider = new FakeInfoProvider(ArtifactsServer);
        var factory = new FakeSessionFactory();

        var client = new DefaultMcpClient(provider, factory);
        await client.DisposeAsync();

        var act = () => client.InvokeAsync("artifacts", "read", null);

        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    private sealed class FakeInfoProvider : IMcpConnectionInfoProvider
    {
        private readonly Dictionary<string, McpServerConnectionInfo> infos;

        public FakeInfoProvider(params McpServerConnectionInfo[] infos)
        {
            this.infos = infos.ToDictionary(i => i.Key, StringComparer.OrdinalIgnoreCase);
        }

        public Task<McpServerConnectionInfo?> GetAsync(string serverKey, CancellationToken cancellationToken = default)
        {
            infos.TryGetValue(serverKey, out var info);
            return Task.FromResult<McpServerConnectionInfo?>(info);
        }
    }

    private sealed class FakeSessionFactory : IMcpSessionFactory
    {
        public List<McpServerConnectionInfo> OpenedInfos { get; } = new();
        public List<FakeSession> Sessions { get; } = new();
        public int OpenCount => OpenedInfos.Count;

        public Task<IMcpSession> OpenAsync(McpServerConnectionInfo info, CancellationToken cancellationToken = default)
        {
            OpenedInfos.Add(info);
            var session = new FakeSession(info.Key);
            Sessions.Add(session);
            return Task.FromResult<IMcpSession>(session);
        }
    }

    private sealed class FakeSession : IMcpSession
    {
        private readonly string serverKey;

        public FakeSession(string serverKey)
        {
            this.serverKey = serverKey;
        }

        public List<(string Tool, string Args)> CallLog { get; } = new();
        public bool Disposed { get; private set; }

        public Task<McpToolResult> CallToolAsync(string toolName, JsonNode? arguments, CancellationToken cancellationToken = default)
        {
            CallLog.Add((toolName, arguments?.ToJsonString() ?? "null"));
            return Task.FromResult(new McpToolResult($"{serverKey}:{toolName}"));
        }

        public Task<IReadOnlyList<McpToolDefinition>> ListToolsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<McpToolDefinition>>(Array.Empty<McpToolDefinition>());
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
