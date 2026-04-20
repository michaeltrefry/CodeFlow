using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeFlow.Runtime;
using CodeFlow.Runtime.Mcp;
using FluentAssertions;
using ModelContextProtocol;

namespace CodeFlow.Runtime.Tests.Mcp;

public sealed class McpToolDiscoveryTests
{
    private static readonly McpServerConnectionInfo TestServer = new(
        Key: "test-server",
        Endpoint: new Uri("https://example.test/mcp"),
        Transport: McpTransportKind.StreamableHttp,
        BearerToken: null);

    [Fact]
    public async Task DiscoverAsync_returns_success_with_tools_when_session_lists_tools()
    {
        var tools = new McpToolDefinition[]
        {
            new("test-server", "read", "reads an artifact", null),
            new("test-server", "write", "writes an artifact", null, IsMutating: true),
        };
        var factory = new StubFactory(new StubSession(tools));

        var discovery = new McpToolDiscovery(factory);

        var result = await discovery.DiscoverAsync(TestServer);

        result.Should().BeOfType<McpDiscoverySuccess>()
            .Which.Tools.Should().BeEquivalentTo(tools);
        factory.LastSession!.Disposed.Should().BeTrue("discovery owns the session and must dispose it");
    }

    [Fact]
    public async Task DiscoverAsync_classifies_HttpRequestException_with_401_as_Authentication()
    {
        var factory = new StubFactory(new StubSession(
            throwOnList: new HttpRequestException("Unauthorized", inner: null, HttpStatusCode.Unauthorized)));

        var discovery = new McpToolDiscovery(factory);

        var result = await discovery.DiscoverAsync(TestServer);

        result.Should().BeOfType<McpDiscoveryFailure>()
            .Which.Kind.Should().Be(McpDiscoveryErrorKind.Authentication);
    }

    [Fact]
    public async Task DiscoverAsync_classifies_HttpRequestException_with_403_as_Authentication()
    {
        var factory = new StubFactory(new StubSession(
            throwOnList: new HttpRequestException("Forbidden", inner: null, HttpStatusCode.Forbidden)));

        var discovery = new McpToolDiscovery(factory);

        var result = await discovery.DiscoverAsync(TestServer);

        result.Should().BeOfType<McpDiscoveryFailure>()
            .Which.Kind.Should().Be(McpDiscoveryErrorKind.Authentication);
    }

    [Fact]
    public async Task DiscoverAsync_classifies_HttpRequestException_with_server_error_as_Unreachable()
    {
        var factory = new StubFactory(new StubSession(
            throwOnList: new HttpRequestException("Bad Gateway", inner: null, HttpStatusCode.BadGateway)));

        var discovery = new McpToolDiscovery(factory);

        var result = await discovery.DiscoverAsync(TestServer);

        result.Should().BeOfType<McpDiscoveryFailure>()
            .Which.Kind.Should().Be(McpDiscoveryErrorKind.Unreachable);
    }

    [Fact]
    public async Task DiscoverAsync_classifies_TaskCanceledException_as_Unreachable()
    {
        var factory = new StubFactory(new StubSession(throwOnList: new TaskCanceledException()));

        var discovery = new McpToolDiscovery(factory);

        var result = await discovery.DiscoverAsync(TestServer);

        result.Should().BeOfType<McpDiscoveryFailure>()
            .Which.Kind.Should().Be(McpDiscoveryErrorKind.Unreachable);
    }

    [Fact]
    public async Task DiscoverAsync_classifies_JsonException_as_Handshake()
    {
        var factory = new StubFactory(new StubSession(throwOnList: new JsonException("bad json")));

        var discovery = new McpToolDiscovery(factory);

        var result = await discovery.DiscoverAsync(TestServer);

        result.Should().BeOfType<McpDiscoveryFailure>()
            .Which.Kind.Should().Be(McpDiscoveryErrorKind.Handshake);
    }

    [Fact]
    public async Task DiscoverAsync_classifies_generic_McpException_as_Handshake()
    {
        var factory = new StubFactory(new StubSession(throwOnList: new McpException("bad response")));

        var discovery = new McpToolDiscovery(factory);

        var result = await discovery.DiscoverAsync(TestServer);

        result.Should().BeOfType<McpDiscoveryFailure>()
            .Which.Kind.Should().Be(McpDiscoveryErrorKind.Handshake);
    }

    [Fact]
    public async Task DiscoverAsync_classifies_protocol_version_McpException_as_ProtocolMismatch()
    {
        var factory = new StubFactory(new StubSession(
            throwOnList: new McpException("Server reports unsupported protocol version 1999-01-01")));

        var discovery = new McpToolDiscovery(factory);

        var result = await discovery.DiscoverAsync(TestServer);

        result.Should().BeOfType<McpDiscoveryFailure>()
            .Which.Kind.Should().Be(McpDiscoveryErrorKind.ProtocolMismatch);
    }

    [Fact]
    public async Task DiscoverAsync_propagates_OperationCanceledException_when_caller_cancels()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var factory = new StubFactory(new StubSession(throwOnList: new OperationCanceledException(cts.Token)));

        var discovery = new McpToolDiscovery(factory);

        var act = () => discovery.DiscoverAsync(TestServer, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task DiscoverAsync_disposes_session_even_when_tool_listing_throws()
    {
        var session = new StubSession(throwOnList: new HttpRequestException("boom"));
        var factory = new StubFactory(session);

        var discovery = new McpToolDiscovery(factory);

        _ = await discovery.DiscoverAsync(TestServer);

        session.Disposed.Should().BeTrue();
    }

    private sealed class StubFactory : IMcpSessionFactory
    {
        private readonly StubSession session;

        public StubFactory(StubSession session)
        {
            this.session = session;
        }

        public StubSession? LastSession { get; private set; }

        public Task<IMcpSession> OpenAsync(McpServerConnectionInfo info, CancellationToken cancellationToken = default)
        {
            LastSession = session;
            return Task.FromResult<IMcpSession>(session);
        }
    }

    private sealed class StubSession : IMcpSession
    {
        private readonly IReadOnlyList<McpToolDefinition>? tools;
        private readonly Exception? throwOnList;

        public StubSession(IReadOnlyList<McpToolDefinition> tools)
        {
            this.tools = tools;
        }

        public StubSession(Exception throwOnList)
        {
            this.throwOnList = throwOnList;
        }

        public bool Disposed { get; private set; }

        public Task<McpToolResult> CallToolAsync(string toolName, JsonNode? arguments, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<McpToolDefinition>> ListToolsAsync(CancellationToken cancellationToken = default)
        {
            if (throwOnList is not null)
            {
                throw throwOnList;
            }

            return Task.FromResult(tools!);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
