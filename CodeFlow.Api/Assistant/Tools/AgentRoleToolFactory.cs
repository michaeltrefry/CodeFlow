using CodeFlow.Runtime;
using CodeFlow.Runtime.Workspace;

namespace CodeFlow.Api.Assistant.Tools;

/// <summary>
/// Turns a <see cref="ResolvedAgentTools"/> bundle (host-tool flags + MCP tool definitions)
/// into a flat list of <see cref="IAssistantTool"/> adapters that the homepage assistant can
/// dispatch alongside its built-in registry. Host-tool adapters bind to a caller-supplied
/// <see cref="ToolWorkspaceContext"/> so the assistant can switch workspaces (default
/// per-conversation dir vs. trace workdir) without rebuilding the registry.
/// </summary>
public sealed class AgentRoleToolFactory
{
    private readonly HostToolProvider hostProvider;
    private readonly IMcpClient mcpClient;

    public AgentRoleToolFactory(
        HostToolProvider hostProvider,
        IMcpClient mcpClient)
    {
        ArgumentNullException.ThrowIfNull(hostProvider);
        ArgumentNullException.ThrowIfNull(mcpClient);

        this.hostProvider = hostProvider;
        this.mcpClient = mcpClient;
    }

    /// <summary>
    /// Build adapters for every tool the role grants. Host-tool adapters share a single
    /// <see cref="ToolExecutionContext"/> wrapping the supplied workspace; pass <c>null</c> when
    /// no workspace is available and host tools will be omitted (with a one-line warning per call,
    /// not per tool). MCP adapters do not need workspace context — the MCP server runs the work —
    /// so they always register when granted, regardless of <paramref name="hostWorkspace"/>.
    /// </summary>
    public IReadOnlyList<IAssistantTool> Build(ToolWorkspaceContext? hostWorkspace, ResolvedAgentTools resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        if ((!resolved.EnableHostTools || resolved.AllowedToolNames.Count == 0)
            && resolved.McpTools.Count == 0)
        {
            return Array.Empty<IAssistantTool>();
        }

        var tools = new List<IAssistantTool>();
        var hostCatalogByName = HostToolProvider.GetCatalog()
            .ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);

        if (resolved.EnableHostTools && hostWorkspace is not null)
        {
            var hostContext = new ToolExecutionContext(Workspace: hostWorkspace);

            foreach (var toolName in resolved.AllowedToolNames)
            {
                if (!hostCatalogByName.TryGetValue(toolName, out var schema))
                {
                    // MCP tool identifiers (mcp:server:tool) live in the same AllowedToolNames set
                    // as host tool names; skip anything that isn't in the host catalog — those are
                    // handled by the McpToolProvider branch below.
                    continue;
                }

                tools.Add(new AgentRoleAssistantTool(schema, hostProvider, () => hostContext));
            }
        }

        if (resolved.McpTools.Count > 0)
        {
            var mcpProvider = new McpToolProvider(mcpClient, resolved.McpTools);
            var mcpSchemas = mcpProvider.AvailableTools(ToolAccessPolicy.AllowAll);

            foreach (var schema in mcpSchemas)
            {
                // MCP tools don't need workspace context (the server runs the work); pass null.
                tools.Add(new AgentRoleAssistantTool(schema, mcpProvider, static () => null));
            }
        }

        return tools;
    }
}
