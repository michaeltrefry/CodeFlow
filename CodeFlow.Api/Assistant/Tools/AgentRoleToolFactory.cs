using CodeFlow.Runtime;
using CodeFlow.Runtime.Workspace;

namespace CodeFlow.Api.Assistant.Tools;

/// <summary>
/// Turns a <see cref="ResolvedAgentTools"/> bundle (host-tool flags + MCP tool definitions)
/// into a flat list of <see cref="IAssistantTool"/> adapters that the homepage assistant can
/// dispatch alongside its built-in registry. The factory builds adapters lazily per conversation
/// so the workspace context is bound to the right per-chat directory.
/// </summary>
public sealed class AgentRoleToolFactory
{
    private readonly HostToolProvider hostProvider;
    private readonly IMcpClient mcpClient;
    private readonly IAssistantWorkspaceProvider workspaceProvider;

    public AgentRoleToolFactory(
        HostToolProvider hostProvider,
        IMcpClient mcpClient,
        IAssistantWorkspaceProvider workspaceProvider)
    {
        ArgumentNullException.ThrowIfNull(hostProvider);
        ArgumentNullException.ThrowIfNull(mcpClient);
        ArgumentNullException.ThrowIfNull(workspaceProvider);

        this.hostProvider = hostProvider;
        this.mcpClient = mcpClient;
        this.workspaceProvider = workspaceProvider;
    }

    /// <summary>
    /// Build adapters for every tool the role grants. Host-tool adapters lazily resolve a
    /// <see cref="ToolWorkspaceContext"/> rooted at <c>{AssistantWorkspaceRoot}/{conversationId:N}</c>;
    /// the directory is created on first invocation, not at adapter construction, so role-less
    /// conversations never hit disk.
    /// </summary>
    public IReadOnlyList<IAssistantTool> Build(Guid conversationId, ResolvedAgentTools resolved)
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

        if (resolved.EnableHostTools)
        {
            // Lazy workspace creation: the directory is only built when a host tool actually runs.
            // Using a closure means every host adapter shares one workspace context per turn.
            ToolExecutionContext? cachedContext = null;
            ToolExecutionContext ContextFactory()
            {
                if (cachedContext is null)
                {
                    var workspace = workspaceProvider.GetOrCreateWorkspace(conversationId);
                    cachedContext = new ToolExecutionContext(Workspace: workspace);
                }
                return cachedContext;
            }

            foreach (var toolName in resolved.AllowedToolNames)
            {
                if (!hostCatalogByName.TryGetValue(toolName, out var schema))
                {
                    // MCP tool identifiers (mcp:server:tool) live in the same AllowedToolNames set
                    // as host tool names; skip anything that isn't in the host catalog — those are
                    // handled by the McpToolProvider branch below.
                    continue;
                }

                tools.Add(new AgentRoleAssistantTool(schema, hostProvider, ContextFactory));
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
