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

    public async Task<ToolResult> InvokeAsync(ToolCall toolCall, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toolCall);

        if (!toolsByName.TryGetValue(toolCall.Name, out var tool))
        {
            throw new UnknownToolException(toolCall.Name);
        }

        var result = await client.InvokeAsync(tool.Server, tool.ToolName, toolCall.Arguments, cancellationToken);
        return new ToolResult(toolCall.Id, result.Content, result.IsError);
    }
}
