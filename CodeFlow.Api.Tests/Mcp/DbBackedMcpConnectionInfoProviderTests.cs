using CodeFlow.Host.Mcp;
using CodeFlow.Persistence;
using CodeFlow.Runtime.Mcp;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeFlow.Api.Tests.Mcp;

/// <summary>
/// Unit tests for <see cref="DbBackedMcpConnectionInfoProvider"/>. The provider is the missing
/// piece between admin-UI MCP server registration and runtime tool invocation: previously the
/// runtime used <see cref="NullMcpConnectionInfoProvider"/> which always returned null, so every
/// role-granted MCP tool threw <see cref="McpServerNotConfiguredException"/> at invocation time
/// despite the admin UI happily showing the server as healthy. These tests exercise the provider
/// in isolation against a stub repository (no DB) — the real repository's case-insensitive lookup
/// is covered separately in <c>McpServerRepositoryTests</c>.
/// </summary>
public sealed class DbBackedMcpConnectionInfoProviderTests
{
    [Fact]
    public async Task GetAsync_returns_repository_payload_for_known_server()
    {
        var info = new McpServerConnectionInfo(
            Key: "codegraph",
            Endpoint: new Uri("https://codegraph.local/mcp"),
            Transport: McpTransportKind.StreamableHttp,
            BearerToken: "token");

        var provider = BuildProvider(new StubRepository(("codegraph", info)));

        var resolved = await provider.GetAsync("codegraph", CancellationToken.None);

        resolved.Should().NotBeNull();
        resolved!.Key.Should().Be("codegraph");
        resolved.Endpoint.Should().Be(new Uri("https://codegraph.local/mcp"));
        resolved.BearerToken.Should().Be("token");
    }

    [Fact]
    public async Task GetAsync_returns_null_for_unknown_server()
    {
        var provider = BuildProvider(new StubRepository());

        var resolved = await provider.GetAsync("missing", CancellationToken.None);

        resolved.Should().BeNull(
            because: "DefaultMcpClient relies on a null return to throw McpServerNotConfiguredException with the offending key");
    }

    [Fact]
    public async Task GetAsync_swallows_repository_failures_and_returns_null()
    {
        // A DB blip during connection-info lookup must not crash the assistant turn — the runtime
        // surfaces the missing server cleanly, the user sees a tool error, and the next request
        // gets a fresh chance.
        var provider = BuildProvider(new ThrowingRepository());

        var resolved = await provider.GetAsync("codegraph", CancellationToken.None);

        resolved.Should().BeNull();
    }

    private static DbBackedMcpConnectionInfoProvider BuildProvider(IMcpServerRepository stub)
    {
        var services = new ServiceCollection();
        services.AddScoped<IMcpServerRepository>(_ => stub);
        var sp = services.BuildServiceProvider();
        return new DbBackedMcpConnectionInfoProvider(
            sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<DbBackedMcpConnectionInfoProvider>.Instance);
    }

    private sealed class StubRepository : IMcpServerRepository
    {
        private readonly Dictionary<string, McpServerConnectionInfo> byKey;

        public StubRepository(params (string Key, McpServerConnectionInfo Info)[] entries)
        {
            byKey = entries.ToDictionary(
                e => e.Key,
                e => e.Info,
                StringComparer.OrdinalIgnoreCase);
        }

        public Task<McpServerConnectionInfo?> GetConnectionInfoAsync(string serverKey, CancellationToken cancellationToken = default)
            => Task.FromResult(byKey.TryGetValue(serverKey, out var info) ? info : null);

        // Other repository members are unused by the provider — throw if anyone calls them.
        public Task<long> CreateAsync(McpServerCreate create, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task UpdateAsync(long id, McpServerUpdate update, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task ArchiveAsync(long id, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<McpServer>> ListAsync(bool includeArchived, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<McpServer?> GetAsync(long id, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<McpServer?> GetByKeyAsync(string key, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task UpdateHealthAsync(long id, McpServerHealthStatus status, DateTime? lastVerifiedAtUtc, string? lastError, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<McpServerTool>> GetToolsAsync(long id, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task ReplaceToolsAsync(long id, IReadOnlyList<McpServerToolWrite> tools, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class ThrowingRepository : IMcpServerRepository
    {
        public Task<McpServerConnectionInfo?> GetConnectionInfoAsync(string serverKey, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("simulated DB failure");

        public Task<long> CreateAsync(McpServerCreate create, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task UpdateAsync(long id, McpServerUpdate update, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task ArchiveAsync(long id, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<McpServer>> ListAsync(bool includeArchived, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<McpServer?> GetAsync(long id, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<McpServer?> GetByKeyAsync(string key, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task UpdateHealthAsync(long id, McpServerHealthStatus status, DateTime? lastVerifiedAtUtc, string? lastError, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<McpServerTool>> GetToolsAsync(long id, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task ReplaceToolsAsync(long id, IReadOnlyList<McpServerToolWrite> tools, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
