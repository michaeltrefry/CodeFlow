using System.Text.Json.Nodes;

namespace CodeFlow.Runtime;

public sealed class HostToolProvider : IToolProvider
{
    private readonly Func<DateTimeOffset> nowProvider;

    public HostToolProvider(Func<DateTimeOffset>? nowProvider = null)
    {
        this.nowProvider = nowProvider ?? (() => DateTimeOffset.UtcNow);
    }

    public ToolCategory Category => ToolCategory.Host;

    public IReadOnlyList<ToolSchema> AvailableTools(ToolAccessPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var limit = policy.GetCategoryLimit(Category);
        if (limit <= 0)
        {
            return [];
        }

        return GetCatalog().Take(limit).ToArray();
    }

    public Task<ToolResult> InvokeAsync(
        ToolCall toolCall,
        AgentInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        ArgumentNullException.ThrowIfNull(context);

        var content = toolCall.Name switch
        {
            "echo" => GetEchoText(toolCall.Arguments),
            "now" => nowProvider().ToString("O"),
            _ => throw new UnknownToolException(toolCall.Name)
        };

        return Task.FromResult(new ToolResult(toolCall.Id, content));
    }

    public static IReadOnlyList<ToolSchema> GetCatalog()
    {
        return
        [
            new ToolSchema(
                "echo",
                "Returns the supplied text unchanged.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["text"] = new JsonObject
                        {
                            ["type"] = "string"
                        }
                    }
                }),
            new ToolSchema(
                "now",
                "Returns the current UTC timestamp in ISO 8601 format.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject()
                })
        ];
    }

    private static string GetEchoText(JsonNode? arguments)
    {
        if (arguments?["text"] is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return text;
        }

        return string.Empty;
    }
}
