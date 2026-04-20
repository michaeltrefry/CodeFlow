namespace CodeFlow.Runtime;

public sealed class ToolRegistry
{
    private readonly IReadOnlyList<IToolProvider> providers;

    public ToolRegistry(IEnumerable<IToolProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        this.providers = providers.ToArray();
    }

    public IReadOnlyList<ToolSchema> AvailableTools(ToolAccessPolicy? policy = null)
    {
        var effectivePolicy = policy ?? ToolAccessPolicy.AllowAll;
        var tools = new List<ToolSchema>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var provider in providers)
        {
            foreach (var tool in provider.AvailableTools(effectivePolicy))
            {
                if (!effectivePolicy.AllowsTool(tool.Name))
                {
                    continue;
                }

                if (!seenNames.Add(tool.Name))
                {
                    throw new InvalidOperationException($"Tool '{tool.Name}' is registered by more than one provider.");
                }

                tools.Add(tool);
            }
        }

        return tools;
    }

    public Task<ToolResult> InvokeAsync(
        ToolCall toolCall,
        ToolAccessPolicy? policy = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toolCall);

        var provider = ResolveProvider(toolCall.Name, policy ?? ToolAccessPolicy.AllowAll);
        return provider.InvokeAsync(toolCall, cancellationToken);
    }

    private IToolProvider ResolveProvider(string toolName, ToolAccessPolicy policy)
    {
        foreach (var provider in providers)
        {
            var hasMatchingTool = provider.AvailableTools(policy)
                .Any(tool => policy.AllowsTool(tool.Name)
                    && string.Equals(tool.Name, toolName, StringComparison.OrdinalIgnoreCase));

            if (hasMatchingTool)
            {
                return provider;
            }
        }

        throw new UnknownToolException(toolName);
    }
}
