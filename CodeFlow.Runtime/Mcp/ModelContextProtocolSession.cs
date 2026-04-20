using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace CodeFlow.Runtime.Mcp;

internal sealed class ModelContextProtocolSession : IMcpSession
{
    private readonly string serverKey;
    private readonly McpClient client;

    public ModelContextProtocolSession(string serverKey, McpClient client)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverKey);
        ArgumentNullException.ThrowIfNull(client);
        this.serverKey = serverKey;
        this.client = client;
    }

    public async Task<McpToolResult> CallToolAsync(
        string toolName,
        JsonNode? arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        var argumentDictionary = ConvertArguments(arguments);
        var result = await client.CallToolAsync(toolName, argumentDictionary, cancellationToken: cancellationToken);

        return new McpToolResult(
            Content: RenderContent(result.Content),
            IsError: result.IsError ?? false);
    }

    public async Task<IReadOnlyList<McpToolDefinition>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);

        var results = new List<McpToolDefinition>(tools.Count);
        foreach (var tool in tools)
        {
            results.Add(new McpToolDefinition(
                Server: serverKey,
                ToolName: tool.Name,
                Description: tool.Description ?? string.Empty,
                Parameters: ConvertSchema(tool.JsonSchema),
                IsMutating: InferIsMutating(tool)));
        }
        return results;
    }

    public async ValueTask DisposeAsync()
    {
        await client.DisposeAsync();
    }

    private static IReadOnlyDictionary<string, object?>? ConvertArguments(JsonNode? arguments)
    {
        if (arguments is null)
        {
            return null;
        }

        if (arguments is not JsonObject jsonObject)
        {
            throw new ArgumentException(
                "MCP tool arguments must be a JSON object at the top level.",
                nameof(arguments));
        }

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in jsonObject)
        {
            result[key] = value is null ? null : JsonSerializer.Deserialize<JsonElement>(value.ToJsonString());
        }
        return result;
    }

    private static JsonNode? ConvertSchema(JsonElement schema)
    {
        if (schema.ValueKind == JsonValueKind.Undefined || schema.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        return JsonNode.Parse(schema.GetRawText());
    }

    private static bool InferIsMutating(McpClientTool tool)
    {
        var annotations = tool.ProtocolTool.Annotations;
        if (annotations is null)
        {
            return true;
        }

        if (annotations.ReadOnlyHint == true)
        {
            return false;
        }

        return true;
    }

    private static string RenderContent(IList<ContentBlock>? content)
    {
        if (content is null || content.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var block in content)
        {
            if (block is TextContentBlock text)
            {
                if (builder.Length > 0)
                {
                    builder.Append('\n');
                }
                builder.Append(text.Text);
            }
        }
        return builder.ToString();
    }
}
