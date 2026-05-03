using System.Text.Json.Nodes;

namespace CodeFlow.Runtime;

public sealed class SubAgentToolProvider : IToolProvider
{
    public const string SpawnToolName = "spawn_subagent";

    private static readonly ToolSchema SpawnSubAgentTool = new(
        SpawnToolName,
        "Spawn one or more anonymous sub-agent workers in parallel. Each invocation provides "
        + "a per-call system prompt (describing the task and the response shape you want) and "
        + "an input. Sub-agents inherit the parent's resolved tool set; the spec on the parent "
        + "agent controls provider/model and concurrency. Returns one result object per "
        + "invocation, in the order requested.",
        new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["invocations"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["systemPrompt"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["description"] = "Instructions and response-shape guidance for "
                                    + "this sub-agent invocation."
                            },
                            ["input"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["description"] = "The task input the sub-agent should act on."
                            }
                        },
                        ["required"] = new JsonArray("systemPrompt", "input")
                    }
                }
            },
            ["required"] = new JsonArray("invocations")
        });

    private readonly Agent agent;
    private readonly AgentInvocationConfiguration parentConfiguration;
    private readonly SubAgentConfig spec;
    private readonly ResolvedAgentTools inheritedTools;

    public SubAgentToolProvider(
        Agent agent,
        AgentInvocationConfiguration parentConfiguration,
        ResolvedAgentTools inheritedTools)
    {
        this.agent = agent ?? throw new ArgumentNullException(nameof(agent));
        this.parentConfiguration = parentConfiguration
            ?? throw new ArgumentNullException(nameof(parentConfiguration));
        this.spec = parentConfiguration.SubAgents
            ?? throw new ArgumentException(
                "Parent configuration must define SubAgents to construct a SubAgentToolProvider.",
                nameof(parentConfiguration));
        this.inheritedTools = inheritedTools ?? throw new ArgumentNullException(nameof(inheritedTools));
    }

    public ToolCategory Category => ToolCategory.SubAgent;

    public IReadOnlyList<ToolSchema> AvailableTools(ToolAccessPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var limit = policy.GetCategoryLimit(Category);
        if (limit <= 0)
        {
            return [];
        }

        return [SpawnSubAgentTool];
    }

    public async Task<ToolResult> InvokeAsync(
        ToolCall toolCall,
        CancellationToken cancellationToken = default,
        ToolExecutionContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(toolCall);

        if (!string.Equals(toolCall.Name, SpawnSubAgentTool.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnknownToolException(toolCall.Name);
        }

        var invocations = ParseInvocations(toolCall.Arguments);
        var maxConcurrent = Math.Max(1, spec.MaxConcurrent);
        using var throttle = new SemaphoreSlim(maxConcurrent, maxConcurrent);

        var childTasks = invocations
            .Select(invocation => InvokeSubAgentThrottledAsync(invocation, throttle, context, cancellationToken))
            .ToArray();

        var results = await Task.WhenAll(childTasks);
        var content = new JsonArray(results.Select(static result => (JsonNode?)result).ToArray()).ToJsonString();

        return new ToolResult(toolCall.Id, content);
    }

    private async Task<JsonObject> InvokeSubAgentThrottledAsync(
        SubAgentInvocation invocation,
        SemaphoreSlim throttle,
        ToolExecutionContext? toolExecutionContext,
        CancellationToken cancellationToken)
    {
        await throttle.WaitAsync(cancellationToken);
        try
        {
            return await InvokeSubAgentAsync(invocation, toolExecutionContext, cancellationToken);
        }
        finally
        {
            throttle.Release();
        }
    }

    private async Task<JsonObject> InvokeSubAgentAsync(
        SubAgentInvocation invocation,
        ToolExecutionContext? toolExecutionContext,
        CancellationToken cancellationToken)
    {
        // Build an ad-hoc child configuration. Provider/model/maxTokens/temperature default to
        // the parent's settings unless overridden on the spec; the per-call systemPrompt comes
        // from the LLM's invocation arguments, since sub-agents are parameterised at spawn time
        // rather than pre-configured per slot. SubAgents on the child is null so children
        // cannot recursively spawn workers themselves.
        var childConfiguration = new AgentInvocationConfiguration(
            Provider: spec.Provider ?? parentConfiguration.Provider,
            Model: spec.Model ?? parentConfiguration.Model,
            SystemPrompt: invocation.SystemPrompt,
            MaxTokens: spec.MaxTokens ?? parentConfiguration.MaxTokens,
            Temperature: spec.Temperature ?? parentConfiguration.Temperature,
            SubAgents: null);

        var result = await agent.InvokeAsync(
            childConfiguration,
            invocation.Input,
            inheritedTools,
            cancellationToken,
            toolExecutionContext);

        return new JsonObject
        {
            ["input"] = invocation.Input,
            ["output"] = result.Output,
            ["decision"] = AgentDecisionJson.ToJsonObject(result.Decision)
        };
    }

    private static IReadOnlyList<SubAgentInvocation> ParseInvocations(JsonNode? arguments)
    {
        if (arguments?["invocations"] is not JsonArray array || array.Count == 0)
        {
            throw new InvalidOperationException("The 'invocations' argument must contain at least one child invocation.");
        }

        return array.Select(ParseInvocation).ToArray();
    }

    private static SubAgentInvocation ParseInvocation(JsonNode? node)
    {
        if (node is null)
        {
            throw new InvalidOperationException("Each child invocation must be an object.");
        }

        return new SubAgentInvocation(
            GetRequiredString(node, "systemPrompt"),
            GetRequiredString(node, "input"));
    }

    private static string GetRequiredString(JsonNode node, string propertyName)
    {
        if (node[propertyName] is JsonValue value
            && value.TryGetValue<string>(out var result)
            && !string.IsNullOrWhiteSpace(result))
        {
            return result;
        }

        throw new InvalidOperationException($"The '{propertyName}' field is required for each child invocation.");
    }

    private sealed record SubAgentInvocation(
        string SystemPrompt,
        string Input);
}
