using System.Text.Json;
using CodeFlow.Api.Assistant.Tools;
using CodeFlow.Api.Tests.Integration;
using CodeFlow.Persistence;
using CodeFlow.Runtime.Mcp;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodeFlow.Api.Tests.Assistant;

/// <summary>
/// Integration tests for the catalog-discovery assistant tools (list_host_tools,
/// list_mcp_servers, list_mcp_server_tools). These mirror /api/host-tools and /api/mcp-servers/*
/// so the assistant can recommend specific tools when a user is authoring an agent role.
/// </summary>
[Trait("Category", "EndToEnd")]
public sealed class ToolDiscoveryToolsTests : IClassFixture<CodeFlowApiFactory>, IAsyncLifetime
{
    private readonly CodeFlowApiFactory factory;

    public ToolDiscoveryToolsTests(CodeFlowApiFactory factory)
    {
        this.factory = factory;
    }

    public async Task InitializeAsync()
    {
        // Each test starts with a clean MCP catalog. The host-tool catalog is static (no DB rows)
        // so it doesn't need clearing.
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();
        db.McpServerTools.RemoveRange(db.McpServerTools);
        db.McpServers.RemoveRange(db.McpServers);
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly JsonElement EmptyArgs = JsonDocument.Parse("{}").RootElement;

    private static JsonElement Args(object obj) =>
        JsonSerializer.SerializeToElement(obj);

    [Fact]
    public async Task ListHostTools_ReturnsStaticCatalog_WithSchemasAndMutatingFlag()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var tool = ResolveTool<ListHostToolsTool>(scope);

        var result = ParseObject(await tool.InvokeAsync(EmptyArgs, CancellationToken.None));

        result.GetProperty("count").GetInt32().Should().BeGreaterThan(0);
        var tools = result.GetProperty("tools");

        var byName = tools.EnumerateArray()
            .ToDictionary(t => t.GetProperty("name").GetString()!, t => t);

        // The catalog ships read_file (read-only) and apply_patch (mutating). Verify both surface
        // with a parsed schema object so the model can introspect required fields.
        byName.Should().ContainKey("read_file");
        byName.Should().ContainKey("apply_patch");

        var readFile = byName["read_file"];
        readFile.GetProperty("isMutating").GetBoolean().Should().BeFalse();
        readFile.GetProperty("parameters").GetProperty("type").GetString().Should().Be("object");
        readFile.GetProperty("parameters").GetProperty("required")[0].GetString().Should().Be("path");

        byName["apply_patch"].GetProperty("isMutating").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task ListMcpServers_ReturnsMetadata_WithToolCounts()
    {
        long server1Id;
        long server2Id;
        await using (var seedScope = factory.Services.CreateAsyncScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();
            var s1 = new McpServerEntity
            {
                Key = "discovery-test-1",
                DisplayName = "Discovery test 1",
                Transport = McpTransportKind.HttpSse,
                EndpointUrl = "https://example.invalid/mcp1",
                HealthStatus = McpServerHealthStatus.Healthy,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
            };
            var s2 = new McpServerEntity
            {
                Key = "discovery-test-2",
                DisplayName = "Discovery test 2",
                Transport = McpTransportKind.HttpSse,
                EndpointUrl = "https://example.invalid/mcp2",
                HealthStatus = McpServerHealthStatus.Unverified,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
            };
            db.McpServers.AddRange(s1, s2);
            await db.SaveChangesAsync();
            server1Id = s1.Id;
            server2Id = s2.Id;

            db.McpServerTools.AddRange(
                new McpServerToolEntity
                {
                    ServerId = server1Id,
                    ToolName = "alpha",
                    Description = "Alpha tool",
                    ParametersJson = """{"type":"object","properties":{}}""",
                    IsMutating = false,
                    SyncedAtUtc = DateTime.UtcNow,
                },
                new McpServerToolEntity
                {
                    ServerId = server1Id,
                    ToolName = "beta",
                    Description = "Beta tool",
                    ParametersJson = """{"type":"object","properties":{}}""",
                    IsMutating = true,
                    SyncedAtUtc = DateTime.UtcNow,
                });
            await db.SaveChangesAsync();
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var tool = ResolveTool<ListMcpServersTool>(scope);

        var result = ParseObject(await tool.InvokeAsync(EmptyArgs, CancellationToken.None));

        var rows = result.GetProperty("servers")
            .EnumerateArray()
            .Where(s =>
            {
                var key = s.GetProperty("key").GetString();
                return key == "discovery-test-1" || key == "discovery-test-2";
            })
            .ToDictionary(s => s.GetProperty("key").GetString()!, s => s);

        rows.Should().ContainKeys("discovery-test-1", "discovery-test-2");
        rows["discovery-test-1"].GetProperty("toolCount").GetInt32().Should().Be(2);
        rows["discovery-test-1"].GetProperty("healthStatus").GetString().Should().Be("Healthy");
        rows["discovery-test-2"].GetProperty("toolCount").GetInt32().Should().Be(0);
        rows["discovery-test-2"].GetProperty("healthStatus").GetString().Should().Be("Unverified");

        // includeArchived toggle should not throw and should at minimum match the default count.
        var withArchived = ParseObject(await tool.InvokeAsync(Args(new { includeArchived = true }), CancellationToken.None));
        withArchived.GetProperty("count").GetInt32()
            .Should().BeGreaterThanOrEqualTo(result.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task ListMcpServerTools_LookupByIdAndKey_ReturnsParsedSchemas()
    {
        long serverId;
        await using (var seedScope = factory.Services.CreateAsyncScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();
            var server = new McpServerEntity
            {
                Key = "discovery-tools-test",
                DisplayName = "Discovery tools test",
                Transport = McpTransportKind.HttpSse,
                EndpointUrl = "https://example.invalid/mcp",
                HealthStatus = McpServerHealthStatus.Healthy,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
            };
            db.McpServers.Add(server);
            await db.SaveChangesAsync();
            serverId = server.Id;

            db.McpServerTools.AddRange(
                new McpServerToolEntity
                {
                    ServerId = serverId,
                    ToolName = "gamma",
                    Description = "Gamma tool",
                    ParametersJson = """{"type":"object","properties":{"q":{"type":"string"}},"required":["q"]}""",
                    IsMutating = false,
                    SyncedAtUtc = DateTime.UtcNow,
                },
                new McpServerToolEntity
                {
                    ServerId = serverId,
                    ToolName = "delta",
                    Description = "Delta tool",
                    ParametersJson = null,
                    IsMutating = true,
                    SyncedAtUtc = DateTime.UtcNow,
                });
            await db.SaveChangesAsync();
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var tool = ResolveTool<ListMcpServerToolsTool>(scope);

        var byId = ParseObject(await tool.InvokeAsync(Args(new { serverId }), CancellationToken.None));
        byId.GetProperty("count").GetInt32().Should().Be(2);
        byId.GetProperty("serverKey").GetString().Should().Be("discovery-tools-test");

        var toolsArr = byId.GetProperty("tools");
        var byName = toolsArr.EnumerateArray()
            .ToDictionary(t => t.GetProperty("name").GetString()!, t => t);

        // gamma has a real schema — confirm it's parsed as a JSON object (not a string).
        var gamma = byName["gamma"];
        gamma.GetProperty("parameters").ValueKind.Should().Be(JsonValueKind.Object);
        gamma.GetProperty("parameters").GetProperty("required")[0].GetString().Should().Be("q");
        gamma.GetProperty("isMutating").GetBoolean().Should().BeFalse();
        gamma.GetProperty("grantIdentifier").GetString()
            .Should().Be("mcp:discovery-tools-test:gamma");

        // delta has no schema. The serializer drops null fields (WhenWritingNull) so the parameters
        // property is absent rather than emitted as null — that's the documented contract for the
        // assistant tool result shape.
        var delta = byName["delta"];
        delta.TryGetProperty("parameters", out _).Should().BeFalse();
        delta.GetProperty("isMutating").GetBoolean().Should().BeTrue();

        // Lookup by key returns the same payload shape.
        var byKey = ParseObject(await tool.InvokeAsync(
            Args(new { serverKey = "discovery-tools-test" }),
            CancellationToken.None));
        byKey.GetProperty("count").GetInt32().Should().Be(2);
        byKey.GetProperty("serverId").GetInt64().Should().Be(serverId);
    }

    [Fact]
    public async Task ListMcpServerTools_RequiresExactlyOneOfIdOrKey()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var tool = ResolveTool<ListMcpServerToolsTool>(scope);

        var neither = await tool.InvokeAsync(EmptyArgs, CancellationToken.None);
        neither.IsError.Should().BeTrue();

        var both = await tool.InvokeAsync(Args(new { serverId = 1, serverKey = "x" }), CancellationToken.None);
        both.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task ListMcpServerTools_NotFound_ReturnsErrorResult()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var tool = ResolveTool<ListMcpServerToolsTool>(scope);

        var result = await tool.InvokeAsync(
            Args(new { serverKey = "no-such-server-xyz" }),
            CancellationToken.None);
        result.IsError.Should().BeTrue();
        result.ResultJson.Should().Contain("not found");
    }

    private static T ResolveTool<T>(AsyncServiceScope scope) where T : IAssistantTool
    {
        var tools = scope.ServiceProvider.GetServices<IAssistantTool>().OfType<T>().ToArray();
        tools.Should().HaveCount(1, $"exactly one {typeof(T).Name} should be registered");
        return tools[0];
    }

    private static JsonElement ParseObject(AssistantToolResult result)
    {
        result.IsError.Should().BeFalse(because: result.ResultJson);
        using var doc = JsonDocument.Parse(result.ResultJson);
        return doc.RootElement.Clone();
    }
}
