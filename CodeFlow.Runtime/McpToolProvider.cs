using System.Text.Json.Nodes;
using CodeFlow.Runtime.Authority;

namespace CodeFlow.Runtime;

public sealed class McpToolProvider : IToolProvider
{
    private readonly IMcpClient client;
    private readonly IReadOnlyDictionary<string, McpToolDefinition> toolsByName;

    public McpToolProvider(IMcpClient client, IReadOnlyList<McpToolDefinition> tools)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        ArgumentNullException.ThrowIfNull(tools);

        toolsByName = tools.ToDictionary(tool => tool.FullName, StringComparer.OrdinalIgnoreCase);
    }

    public ToolCategory Category => ToolCategory.Mcp;

    public IReadOnlyList<ToolSchema> AvailableTools(ToolAccessPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var limit = policy.GetCategoryLimit(Category);
        if (limit <= 0)
        {
            return [];
        }

        return toolsByName.Values
            .Select(static tool => new ToolSchema(
                tool.FullName,
                tool.Description,
                tool.Parameters?.DeepClone(),
                tool.IsMutating))
            .Take(limit)
            .ToArray();
    }

    public async Task<ToolResult> InvokeAsync(
        ToolCall toolCall,
        CancellationToken cancellationToken = default,
        ToolExecutionContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(toolCall);

        if (!toolsByName.TryGetValue(toolCall.Name, out var tool))
        {
            throw new UnknownToolException(toolCall.Name);
        }

        // sc-269 PR3: MCP tool calls cross the network boundary, so they're gated by the
        // envelope's Network axis. NetworkPolicy.None refuses every MCP invocation; Loopback
        // and Allowlist allow the call through (per-host filtering against AllowedHosts is
        // deferred to a follow-up — the MCP client doesn't currently surface the server URL
        // to this layer in a uniform way). When the envelope is silent (null), behaviour is
        // unchanged from pre-PR3.
        if (context?.Envelope?.Network is { Allow: NetworkPolicy.None })
        {
            return BuildNetworkRefusal(toolCall);
        }

        var result = await client.InvokeAsync(tool.Server, tool.ToolName, toolCall.Arguments, cancellationToken);
        return new ToolResult(toolCall.Id, result.Content, result.IsError);
    }

    private static ToolResult BuildNetworkRefusal(ToolCall toolCall)
    {
        return new ToolResult(
            toolCall.Id,
            new JsonObject
            {
                ["ok"] = false,
                ["refusal"] = new JsonObject
                {
                    ["code"] = "envelope-network",
                    ["reason"] = $"MCP tool '{toolCall.Name}' was refused: the run's Network envelope axis denies network egress.",
                    ["axis"] = BlockedBy.Axes.Network,
                    ["tool"] = toolCall.Name,
                    ["policy"] = NetworkPolicy.None.ToString()
                }
            }.ToJsonString(),
            IsError: true);
    }
}
