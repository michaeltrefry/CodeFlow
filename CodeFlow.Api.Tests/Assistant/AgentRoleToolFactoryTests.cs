using System.Text.Json.Nodes;
using CodeFlow.Api.Assistant.Tools;
using CodeFlow.Runtime;
using CodeFlow.Runtime.Workspace;
using FluentAssertions;

namespace CodeFlow.Api.Tests.Assistant;

/// <summary>
/// Unit tests for the host-vs-MCP gating in <see cref="AgentRoleToolFactory.Build"/>. The bug
/// these tests guard against: previously the homepage assistant required a writable workspace
/// before *any* role-tool merge could happen, which silently suppressed MCP grants in dev /
/// misconfigured prod environments where the assistant workspace dir wasn't usable. MCP tools
/// don't need a workspace and must register independently.
/// </summary>
public sealed class AgentRoleToolFactoryTests
{
    [Fact]
    public void Build_WithMcpOnlyRole_AndNullWorkspace_ReturnsMcpAdapters()
    {
        var factory = new AgentRoleToolFactory(new HostToolProvider(), new StubMcpClient());
        var resolved = new ResolvedAgentTools(
            AllowedToolNames: new[] { "mcp:codegraph:search_graph" },
            McpTools: new[]
            {
                new McpToolDefinition(
                    Server: "codegraph",
                    ToolName: "search_graph",
                    Description: "graph search",
                    Parameters: new JsonObject { ["type"] = "object" },
                    IsMutating: false),
            },
            EnableHostTools: false);

        var tools = factory.Build(hostWorkspace: null, resolved);

        tools.Should().HaveCount(1, because: "MCP grants must merge regardless of workspace state");
        tools[0].Name.Should().Be("mcp_codegraph_search_graph");
    }

    [Fact]
    public void Build_WithHostOnlyRole_AndNullWorkspace_ReturnsEmpty()
    {
        var factory = new AgentRoleToolFactory(new HostToolProvider(), new StubMcpClient());
        var resolved = new ResolvedAgentTools(
            AllowedToolNames: new[] { "read_file" },
            McpTools: Array.Empty<McpToolDefinition>(),
            EnableHostTools: true);

        var tools = factory.Build(hostWorkspace: null, resolved);

        tools.Should().BeEmpty(
            because: "host tools require a workspace and must be silently dropped when the runtime can't provide one");
    }

    [Fact]
    public void Build_WithMixedRole_AndNullWorkspace_ReturnsOnlyMcpAdapters()
    {
        var factory = new AgentRoleToolFactory(new HostToolProvider(), new StubMcpClient());
        var resolved = new ResolvedAgentTools(
            AllowedToolNames: new[] { "read_file", "mcp:codegraph:search_graph" },
            McpTools: new[]
            {
                new McpToolDefinition(
                    Server: "codegraph",
                    ToolName: "search_graph",
                    Description: "graph search",
                    Parameters: new JsonObject { ["type"] = "object" },
                    IsMutating: false),
            },
            EnableHostTools: true);

        var tools = factory.Build(hostWorkspace: null, resolved);

        tools.Should().HaveCount(1);
        tools[0].Name.Should().Be("mcp_codegraph_search_graph");
    }

    [Fact]
    public void Build_WithMixedRole_AndConcreteWorkspace_ReturnsBothHostAndMcp()
    {
        var factory = new AgentRoleToolFactory(new HostToolProvider(), new StubMcpClient());
        var workspace = new ToolWorkspaceContext(Guid.NewGuid(), Path.GetTempPath());
        var resolved = new ResolvedAgentTools(
            AllowedToolNames: new[] { "read_file", "mcp:codegraph:search_graph" },
            McpTools: new[]
            {
                new McpToolDefinition(
                    Server: "codegraph",
                    ToolName: "search_graph",
                    Description: "graph search",
                    Parameters: new JsonObject { ["type"] = "object" },
                    IsMutating: false),
            },
            EnableHostTools: true);

        var tools = factory.Build(workspace, resolved);

        tools.Select(t => t.Name).Should().BeEquivalentTo(new[]
        {
            "read_file",
            "mcp_codegraph_search_graph",
        });
    }

    private sealed class StubMcpClient : IMcpClient
    {
        public Task<McpToolResult> InvokeAsync(string server, string toolName, JsonNode? arguments, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("StubMcpClient is for adapter wiring tests only — Build() does not invoke.");
    }
}
