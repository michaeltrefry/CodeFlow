using System.Text.Json.Nodes;

namespace CodeFlow.Runtime;

public sealed class SubAgentToolProvider : IToolProvider
{
    public const string SpawnToolName = "spawn_subagent";

    private static readonly ToolSchema SpawnSubAgentTool = new(
        SpawnToolName,
        "Invoke one or more configured child agents in parallel and return their results.",
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
                            ["agent"] = new JsonObject
                            {
                                ["type"] = "string"
                            },
                            ["input"] = new JsonObject
                            {
                                ["type"] = "string"
                            }
                        },
                        ["required"] = new JsonArray("agent", "input")
                    }
                }
            },
            ["required"] = new JsonArray("invocations")
        });

    private readonly Agent agent;
    private readonly IReadOnlyDictionary<string, AgentInvocationConfiguration> subAgents;
    private readonly ResolvedAgentTools inheritedTools;

    public SubAgentToolProvider(
        Agent agent,
        IReadOnlyDictionary<string, AgentInvocationConfiguration> subAgents,
        ResolvedAgentTools inheritedTools)
    {
        this.agent = agent ?? throw new ArgumentNullException(nameof(agent));
        this.subAgents = subAgents ?? throw new ArgumentNullException(nameof(subAgents));
        this.inheritedTools = inheritedTools ?? throw new ArgumentNullException(nameof(inheritedTools));
    }

    public ToolCategory Category => ToolCategory.SubAgent;

    public IReadOnlyList<ToolSchema> AvailableTools(ToolAccessPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var limit = policy.GetCategoryLimit(Category);
        if (limit <= 0 || subAgents.Count == 0)
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
        var childTasks = invocations
            .Select(invocation => InvokeSubAgentAsync(invocation, context, cancellationToken))
            .ToArray();

        var results = await Task.WhenAll(childTasks);
        var content = new JsonArray(results.Select(static result => result).ToArray()).ToJsonString();

        return new ToolResult(toolCall.Id, content);
    }

    private async Task<JsonObject> InvokeSubAgentAsync(
        SubAgentInvocation invocation,
        ToolExecutionContext? toolExecutionContext,
        CancellationToken cancellationToken)
    {
        if (!subAgents.TryGetValue(invocation.Agent, out var configuration))
        {
            throw new InvalidOperationException($"Unknown sub-agent '{invocation.Agent}'.");
        }

        var result = await agent.InvokeAsync(
            configuration,
            invocation.Input,
            inheritedTools,
            cancellationToken,
            toolExecutionContext);

        return new JsonObject
        {
            ["agent"] = invocation.Agent,
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
            GetRequiredString(node, "agent"),
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
        string Agent,
        string Input);
}
