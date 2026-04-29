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
    private readonly string runtimeName;

    public AgentRoleAssistantTool(
        ToolSchema schema,
        IToolProvider provider,
        Func<ToolExecutionContext?> contextFactory)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(contextFactory);

        // Anthropic + OpenAI tool-calling APIs only accept names matching ^[a-zA-Z0-9_-]{1,128}$,
        // but the runtime catalog uses dotted names (vcs.get_repo, mcp:server:tool) and colons.
        // Sanitize the LLM-facing name; preserve the original for runtime dispatch via runtimeName.
        runtimeName = schema.Name;
        Name = SanitizeName(schema.Name);
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
            Name: runtimeName,
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
                JsonSerializer.Serialize(new { error = $"Tool '{runtimeName}' is not available in the runtime catalog." }),
                IsError: true);
        }

        return new AssistantToolResult(result.Content, result.IsError);
    }

    /// <summary>
    /// Replaces any character outside <c>[a-zA-Z0-9_-]</c> with an underscore. Both Anthropic and
    /// OpenAI reject tool names with dots/colons, which collide with the runtime's catalog
    /// (<c>vcs.get_repo</c>) and the MCP convention (<c>mcp:server:tool</c>).
    /// </summary>
    internal static string SanitizeName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        var needsSanitize = false;
        foreach (var ch in name)
        {
            if (!IsValidNameChar(ch))
            {
                needsSanitize = true;
                break;
            }
        }
        if (!needsSanitize)
        {
            return name;
        }

        return string.Create(name.Length, name, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var ch = source[i];
                span[i] = IsValidNameChar(ch) ? ch : '_';
            }
        });
    }

    private static bool IsValidNameChar(char ch)
        => (ch >= 'a' && ch <= 'z')
            || (ch >= 'A' && ch <= 'Z')
            || (ch >= '0' && ch <= '9')
            || ch == '_'
            || ch == '-';

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
