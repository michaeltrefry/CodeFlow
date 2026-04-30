using CodeFlow.Runtime.Authority;

namespace CodeFlow.Runtime;

public sealed class ToolRegistry
{
    private readonly IReadOnlyList<IToolProvider> providers;
    private readonly IRefusalEventSink refusalSink;
    private readonly Func<DateTimeOffset> nowProvider;

    public ToolRegistry(IEnumerable<IToolProvider> providers)
        : this(providers, refusalSink: null, nowProvider: null)
    {
    }

    public ToolRegistry(
        IEnumerable<IToolProvider> providers,
        IRefusalEventSink? refusalSink,
        Func<DateTimeOffset>? nowProvider)
    {
        ArgumentNullException.ThrowIfNull(providers);

        this.providers = providers.ToArray();
        this.refusalSink = refusalSink ?? NullRefusalEventSink.Instance;
        this.nowProvider = nowProvider ?? (() => DateTimeOffset.UtcNow);
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

    public async Task<ToolResult> InvokeAsync(
        ToolCall toolCall,
        ToolAccessPolicy? policy = null,
        CancellationToken cancellationToken = default,
        ToolExecutionContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(toolCall);

        var provider = ResolveProvider(toolCall.Name, policy ?? ToolAccessPolicy.AllowAll);
        var result = await provider.InvokeAsync(toolCall, cancellationToken, context);
        await TryRecordRefusalAsync(toolCall, context, result, cancellationToken);
        return result;
    }

    private async Task TryRecordRefusalAsync(
        ToolCall toolCall,
        ToolExecutionContext? context,
        ToolResult result,
        CancellationToken cancellationToken)
    {
        if (!result.IsError)
        {
            return;
        }

        var parsed = RefusalPayloadParser.TryParse(result.Content);
        if (parsed is null)
        {
            return;
        }

        var workspaceCorrelation = context?.Workspace?.CorrelationId;
        var traceId = workspaceCorrelation == Guid.Empty ? (Guid?)null : workspaceCorrelation;

        var refusal = new RefusalEvent(
            Id: Guid.NewGuid(),
            TraceId: traceId,
            AssistantConversationId: null,
            Stage: RefusalStages.Tool,
            Code: parsed.Code,
            Reason: parsed.Reason,
            Axis: parsed.Axis,
            Path: parsed.Path ?? toolCall.Name,
            DetailJson: parsed.DetailJson,
            OccurredAt: nowProvider());

        try
        {
            await refusalSink.RecordAsync(refusal, cancellationToken);
        }
        catch
        {
            // Refusal recording must never break the calling tool's primary failure flow.
            // The structured payload is already in the ToolResult that we return.
        }
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
