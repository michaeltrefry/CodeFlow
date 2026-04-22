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
    private readonly Uri responsesEndpoint;
    private readonly string? apiKey;
    private readonly int maxRetryAttempts;
    private readonly TimeSpan initialRetryDelay;

    protected OpenAiCompatibleResponsesModelClientBase(
        HttpClient httpClient,
        Uri responsesEndpoint,
        string? apiKey,
        int maxRetryAttempts,
        TimeSpan initialRetryDelay)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.responsesEndpoint = responsesEndpoint ?? throw new ArgumentNullException(nameof(responsesEndpoint));
        this.apiKey = apiKey;
        this.maxRetryAttempts = maxRetryAttempts;
        this.initialRetryDelay = initialRetryDelay;
    }

    public async Task<InvocationResponse> InvokeAsync(
        InvocationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var toolNameMap = ProviderToolNameMap.Create(request.Tools);
        using var httpRequest = BuildHttpRequest(request, toolNameMap);
        using var response = await SendWithRetryAsync(httpRequest, cancellationToken);

        await ModelClientHttpErrorHelper.EnsureSuccessStatusCodeAsync(httpRequest, response, cancellationToken);

        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(contentStream, cancellationToken: cancellationToken);

        return ParseInvocationResponse(document.RootElement, toolNameMap);
    }

    protected virtual void ApplyHeaders(HttpRequestMessage httpRequest)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
    }

    private HttpRequestMessage BuildHttpRequest(InvocationRequest request, ProviderToolNameMap toolNameMap)
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

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, responsesEndpoint)
        {
            Content = new StringContent(payload.ToJsonString(SerializerOptions), Encoding.UTF8, "application/json")
        };

        ApplyHeaders(httpRequest);
        return httpRequest;
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var attempt = 0;

        while (true)
        {
            attempt++;

            var requestClone = await CloneRequestAsync(request, cancellationToken);

            try
            {
                var response = await httpClient.SendAsync(requestClone, cancellationToken);

                if (!ShouldRetry(response.StatusCode) || attempt >= maxRetryAttempts)
                {
                    return response;
                }

                var delay = GetRetryDelay(response, attempt);
                response.Dispose();
                await Task.Delay(delay, cancellationToken);
            }
            catch (HttpRequestException) when (attempt < maxRetryAttempts)
            {
                await Task.Delay(GetRetryDelay(response: null, attempt), cancellationToken);
            }
        }
    }

    private static bool ShouldRetry(HttpStatusCode statusCode)
    {
        return statusCode == HttpStatusCode.TooManyRequests
            || statusCode == HttpStatusCode.RequestTimeout
            || statusCode == HttpStatusCode.BadGateway
            || statusCode == HttpStatusCode.ServiceUnavailable
            || statusCode == HttpStatusCode.GatewayTimeout
            || (int)statusCode >= 500;
    }

    private TimeSpan GetRetryDelay(HttpResponseMessage? response, int attempt)
    {
        if (response?.Headers.RetryAfter?.Delta is TimeSpan retryAfterDelta)
        {
            return retryAfterDelta;
        }

        if (response?.Headers.RetryAfter?.Date is DateTimeOffset retryAfterDate)
        {
            var delta = retryAfterDate - DateTimeOffset.UtcNow;
            return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
        }

        var multiplier = Math.Pow(2, Math.Max(0, attempt - 1));
        var calculatedDelay = TimeSpan.FromMilliseconds(initialRetryDelay.TotalMilliseconds * multiplier);

        return calculatedDelay > TimeSpan.Zero ? calculatedDelay : TimeSpan.Zero;
    }

    private static async Task<HttpRequestMessage> CloneRequestAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);

        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (request.Content is not null)
        {
            var content = await request.Content.ReadAsStringAsync(cancellationToken);
            clone.Content = new StringContent(content, Encoding.UTF8, request.Content.Headers.ContentType?.MediaType);

            foreach (var header in request.Content.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return clone;
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
            ParseTokenUsage(root));
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
