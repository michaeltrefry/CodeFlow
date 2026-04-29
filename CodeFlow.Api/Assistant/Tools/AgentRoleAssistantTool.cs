using System.Text.Json;
using System.Text.Json.Nodes;
using CodeFlow.Runtime;
using CodeFlow.Runtime.Workspace;

namespace CodeFlow.Api.Assistant.Tools;

/// <summary>
/// Adapter that exposes a single agent-role-granted host or MCP tool through the homepage
/// assistant's <see cref="IAssistantTool"/> contract. The dispatcher invokes this just like a
/// built-in tool (e.g. <c>list_workflows</c>); under the hood it calls the runtime's
/// <see cref="IToolProvider"/> with a workspace context bound to the current conversation so
/// host tools (read_file, apply_patch, run_command) operate against
/// <c>{AssistantWorkspaceRoot}/{conversationId:N}</c>.
/// </summary>
/// <remarks>
/// One instance per granted tool — the LLM tool registry is the union of built-in
/// IAssistantTool implementations and these adapters.
/// </remarks>
public sealed class AgentRoleAssistantTool : IAssistantTool
{
    private readonly IToolProvider provider;
    private readonly Func<ToolExecutionContext?> contextFactory;
    private readonly JsonElement inputSchema;

    public AgentRoleAssistantTool(
        ToolSchema schema,
        IToolProvider provider,
        Func<ToolExecutionContext?> contextFactory)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(contextFactory);

        Name = schema.Name;
        Description = string.IsNullOrWhiteSpace(schema.Description)
            ? schema.Name
            : schema.Description;
        this.provider = provider;
        this.contextFactory = contextFactory;
        inputSchema = ConvertSchema(schema.Parameters);
    }

    public string Name { get; }

    public string Description { get; }

    public JsonElement InputSchema => inputSchema;

    public async Task<AssistantToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var argumentsNode = ConvertArguments(arguments);
        var toolCall = new ToolCall(
            Id: Guid.NewGuid().ToString("N"),
            Name: Name,
            Arguments: argumentsNode);

        ToolResult result;
        try
        {
            result = await provider.InvokeAsync(toolCall, cancellationToken, contextFactory());
        }
        catch (UnknownToolException)
        {
            // The provider doesn't recognize the tool — surface as a structured tool error so the
            // model can recover rather than crashing the turn.
            return new AssistantToolResult(
                JsonSerializer.Serialize(new { error = $"Tool '{Name}' is not available in the runtime catalog." }),
                IsError: true);
        }

        return new AssistantToolResult(result.Content, result.IsError);
    }

    private static JsonElement ConvertSchema(JsonNode? parameters)
    {
        if (parameters is null)
        {
            // The dispatcher requires a JSON object; an empty schema means "no inputs".
            using var doc = JsonDocument.Parse("""{"type":"object","properties":{}}""");
            return doc.RootElement.Clone();
        }

        using var docFromNode = JsonDocument.Parse(parameters.ToJsonString());
        return docFromNode.RootElement.Clone();
    }

    private static JsonNode? ConvertArguments(JsonElement arguments)
    {
        if (arguments.ValueKind == JsonValueKind.Undefined || arguments.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return JsonNode.Parse(arguments.GetRawText());
    }
}
