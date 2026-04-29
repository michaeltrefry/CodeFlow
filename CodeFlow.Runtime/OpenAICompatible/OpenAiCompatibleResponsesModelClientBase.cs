using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeFlow.Runtime.OpenAICompatible;

public abstract class OpenAiCompatibleResponsesModelClientBase : IModelClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient httpClient;
    private readonly Func<OpenAiCompatibleResponsesRuntimeOptions> optionsResolver;

    protected OpenAiCompatibleResponsesModelClientBase(
        HttpClient httpClient,
        Func<OpenAiCompatibleResponsesRuntimeOptions> optionsResolver)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.optionsResolver = optionsResolver ?? throw new ArgumentNullException(nameof(optionsResolver));
    }

    public async Task<InvocationResponse> InvokeAsync(
        InvocationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var runtimeOptions = optionsResolver()
            ?? throw new InvalidOperationException("Options resolver returned null.");
        EnsureUsable(runtimeOptions);

        var toolNameMap = ProviderToolNameMap.Create(request.Tools);
        using var httpRequest = BuildHttpRequest(request, runtimeOptions, toolNameMap);
        using var response = await ChatModelHttpRetry.SendWithRetryAsync(
            httpClient,
            httpRequest,
            maxRetryAttempts: runtimeOptions.MaxRetryAttempts,
            initialRetryDelay: runtimeOptions.InitialRetryDelay,
            extraRetryStatusCheck: null,
            extraRetryAfterExtractor: null,
            cancellationToken);

        await ModelClientHttpErrorHelper.EnsureSuccessStatusCodeAsync(httpRequest, response, cancellationToken);

        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(contentStream, cancellationToken: cancellationToken);

        return ParseInvocationResponse(document.RootElement, toolNameMap);
    }

    protected virtual void ApplyHeaders(HttpRequestMessage httpRequest, OpenAiCompatibleResponsesRuntimeOptions runtimeOptions)
    {
        if (!string.IsNullOrWhiteSpace(runtimeOptions.ApiKey))
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", runtimeOptions.ApiKey);
        }
    }

    protected virtual void EnsureUsable(OpenAiCompatibleResponsesRuntimeOptions runtimeOptions)
    {
    }

    private HttpRequestMessage BuildHttpRequest(
        InvocationRequest request,
        OpenAiCompatibleResponsesRuntimeOptions runtimeOptions,
        ProviderToolNameMap toolNameMap)
    {
        var payload = new JsonObject
        {
            ["model"] = request.Model,
            ["input"] = BuildInputItems(request.Messages, toolNameMap),
            ["store"] = false
        };

        if (request.Tools is { Count: > 0 })
        {
            payload["tools"] = BuildTools(request.Tools, toolNameMap);
        }

        if (request.MaxTokens is int maxTokens)
        {
            payload["max_output_tokens"] = maxTokens;
        }

        if (request.Temperature is double temperature)
        {
            payload["temperature"] = temperature;
        }

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, runtimeOptions.ResponsesEndpoint)
        {
            Content = new StringContent(payload.ToJsonString(SerializerOptions), Encoding.UTF8, "application/json")
        };

        ApplyHeaders(httpRequest, runtimeOptions);
        return httpRequest;
    }

    private static JsonArray BuildInputItems(IReadOnlyList<ChatMessage> messages, ProviderToolNameMap toolNameMap)
    {
        var inputItems = new JsonArray();

        foreach (var message in messages)
        {
            if (message.Role == ChatMessageRole.Tool)
            {
                if (string.IsNullOrWhiteSpace(message.ToolCallId))
                {
                    throw new InvalidOperationException("Tool messages require a tool call id.");
                }

                inputItems.Add(new JsonObject
                {
                    ["type"] = "function_call_output",
                    ["call_id"] = message.ToolCallId,
                    ["output"] = message.Content
                });

                continue;
            }

            if (!string.IsNullOrWhiteSpace(message.Content))
            {
                inputItems.Add(new JsonObject
                {
                    ["type"] = "message",
                    ["role"] = ToOpenAiRole(message.Role),
                    ["content"] = message.Content
                });
            }

            if (message.ToolCalls is null)
            {
                continue;
            }

            foreach (var toolCall in message.ToolCalls)
            {
                inputItems.Add(new JsonObject
                {
                    ["type"] = "function_call",
                    ["call_id"] = toolCall.Id,
                    ["name"] = toolNameMap.ToProviderName(toolCall.Name),
                    ["arguments"] = SerializeArguments(toolCall.Arguments)
                });
            }
        }

        return inputItems;
    }

    private static JsonArray BuildTools(IReadOnlyList<ToolSchema> tools, ProviderToolNameMap toolNameMap)
    {
        var jsonTools = new JsonArray();

        foreach (var tool in tools)
        {
            var toolObject = new JsonObject
            {
                ["type"] = "function",
                ["name"] = toolNameMap.ToProviderName(tool.Name)
            };

            if (!string.IsNullOrWhiteSpace(tool.Description))
            {
                toolObject["description"] = tool.Description;
            }

            if (tool.Parameters is not null)
            {
                toolObject["parameters"] = tool.Parameters.DeepClone();
            }

            jsonTools.Add(toolObject);
        }

        return jsonTools;
    }

    private static InvocationResponse ParseInvocationResponse(JsonElement root, ProviderToolNameMap toolNameMap)
    {
        var toolCalls = new List<ToolCall>();
        var textParts = new List<string>();

        if (root.TryGetProperty("output", out var outputElement) && outputElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in outputElement.EnumerateArray())
            {
                if (!item.TryGetProperty("type", out var itemTypeElement))
                {
                    continue;
                }

                var itemType = itemTypeElement.GetString();

                if (string.Equals(itemType, "message", StringComparison.OrdinalIgnoreCase))
                {
                    AppendMessageText(item, textParts);
                    continue;
                }

                if (string.Equals(itemType, "function_call", StringComparison.OrdinalIgnoreCase))
                {
                    toolCalls.Add(ParseToolCall(item, toolNameMap));
                }
            }
        }

        var stopReason = toolCalls.Count > 0
            ? InvocationStopReason.ToolCalls
            : ParseStopReason(root);

        var assistantMessage = new ChatMessage(
            ChatMessageRole.Assistant,
            string.Join(Environment.NewLine, textParts.Where(static part => !string.IsNullOrWhiteSpace(part))),
            toolCalls.Count > 0 ? toolCalls : null);

        return new InvocationResponse(
            assistantMessage,
            stopReason,
            ParseTokenUsage(root),
            CloneRawUsage(root));
    }

    /// <summary>
    /// Clone the provider's <c>usage</c> object verbatim so the token-usage capture observer can
    /// persist every reported field — including ones the schema-flat <see cref="TokenUsage"/>
    /// drops (cache_creation_input_tokens, cache_read_input_tokens, output_tokens_details with
    /// reasoning_tokens, etc.) and any future fields. Returns null when the response has no
    /// usage object at all (capture is skipped).
    /// </summary>
    private static JsonElement? CloneRawUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usageElement) || usageElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        return usageElement.Clone();
    }

    private static void AppendMessageText(JsonElement messageItem, List<string> textParts)
    {
        if (!messageItem.TryGetProperty("content", out var contentElement) || contentElement.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var contentItem in contentElement.EnumerateArray())
        {
            if (!contentItem.TryGetProperty("type", out var contentTypeElement))
            {
                continue;
            }

            var contentType = contentTypeElement.GetString();

            if ((string.Equals(contentType, "output_text", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(contentType, "text", StringComparison.OrdinalIgnoreCase))
                && contentItem.TryGetProperty("text", out var textElement))
            {
                var text = textElement.GetString();

                if (!string.IsNullOrWhiteSpace(text))
                {
                    textParts.Add(text);
                }
            }
        }
    }

    private static ToolCall ParseToolCall(JsonElement item, ProviderToolNameMap toolNameMap)
    {
        var callId = item.TryGetProperty("call_id", out var callIdElement)
            ? callIdElement.GetString()
            : null;

        var itemId = item.TryGetProperty("id", out var itemIdElement)
            ? itemIdElement.GetString()
            : null;

        var arguments = item.TryGetProperty("arguments", out var argumentsElement)
            ? ParseArguments(argumentsElement.GetString())
            : null;

        return new ToolCall(
            callId ?? itemId ?? throw new InvalidOperationException("Function call output is missing an id."),
            toolNameMap.ToInternalName(item.GetProperty("name").GetString() ?? throw new InvalidOperationException("Function call output is missing a name.")),
            arguments);
    }

    private static InvocationStopReason ParseStopReason(JsonElement root)
    {
        if (root.TryGetProperty("incomplete_details", out var incompleteDetails)
            && incompleteDetails.ValueKind == JsonValueKind.Object
            && incompleteDetails.TryGetProperty("reason", out var reasonElement))
        {
            return reasonElement.GetString() switch
            {
                "max_output_tokens" => InvocationStopReason.MaxTokens,
                "content_filter" => InvocationStopReason.ContentFilter,
                _ => InvocationStopReason.Unknown
            };
        }

        return InvocationStopReason.EndTurn;
    }

    private static TokenUsage? ParseTokenUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usageElement) || usageElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new TokenUsage(
            usageElement.TryGetProperty("input_tokens", out var inputTokens) ? inputTokens.GetInt32() : 0,
            usageElement.TryGetProperty("output_tokens", out var outputTokens) ? outputTokens.GetInt32() : 0,
            usageElement.TryGetProperty("total_tokens", out var totalTokens) ? totalTokens.GetInt32() : 0);
    }

    private static string ToOpenAiRole(ChatMessageRole role)
    {
        return role switch
        {
            ChatMessageRole.System => "system",
            ChatMessageRole.User => "user",
            ChatMessageRole.Assistant => "assistant",
            _ => throw new InvalidOperationException($"Unsupported OpenAI role mapping for '{role}'.")
        };
    }

    private static string SerializeArguments(JsonNode? arguments)
    {
        return arguments?.ToJsonString(SerializerOptions) ?? "{}";
    }

    private static JsonNode? ParseArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return null;
        }

        return JsonNode.Parse(arguments);
    }
}
