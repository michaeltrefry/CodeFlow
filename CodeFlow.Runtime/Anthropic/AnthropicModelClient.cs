using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeFlow.Runtime.Anthropic;

public sealed class AnthropicModelClient : IModelClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient httpClient;
    private readonly AnthropicModelClientOptions options;

    public AnthropicModelClient(HttpClient httpClient, AnthropicModelClientOptions options)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.options = options ?? throw new ArgumentNullException(nameof(options));

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new ArgumentException("An Anthropic API key is required.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.ApiVersion))
        {
            throw new ArgumentException("An Anthropic API version is required.", nameof(options));
        }
    }

    public async Task<InvocationResponse> InvokeAsync(
        InvocationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var httpRequest = BuildHttpRequest(request);
        using var response = await SendWithRetryAsync(httpRequest, cancellationToken);

        response.EnsureSuccessStatusCode();

        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(contentStream, cancellationToken: cancellationToken);

        return ParseInvocationResponse(document.RootElement);
    }

    private HttpRequestMessage BuildHttpRequest(InvocationRequest request)
    {
        var payload = new JsonObject
        {
            ["model"] = request.Model,
            ["max_tokens"] = request.MaxTokens ?? 1024,
            ["messages"] = BuildMessages(request.Messages)
        };

        var systemPrompt = BuildSystemPrompt(request.Messages);

        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            payload["system"] = systemPrompt;
        }

        if (request.Tools is { Count: > 0 })
        {
            payload["tools"] = BuildTools(request.Tools);
        }

        if (request.Temperature is double temperature)
        {
            payload["temperature"] = temperature;
        }

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, options.MessagesEndpoint)
        {
            Content = new StringContent(payload.ToJsonString(SerializerOptions), Encoding.UTF8, "application/json")
        };

        httpRequest.Headers.TryAddWithoutValidation("x-api-key", options.ApiKey);
        httpRequest.Headers.TryAddWithoutValidation("anthropic-version", options.ApiVersion);

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

                if (!ShouldRetry(response.StatusCode) || attempt >= options.MaxRetryAttempts)
                {
                    return response;
                }

                var delay = GetRetryDelay(response, attempt);
                response.Dispose();
                await Task.Delay(delay, cancellationToken);
            }
            catch (HttpRequestException) when (attempt < options.MaxRetryAttempts)
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
            || (int)statusCode == 529
            || (int)statusCode >= 500;
    }

    private TimeSpan GetRetryDelay(HttpResponseMessage? response, int attempt)
    {
        if (response?.Headers.RetryAfter?.Delta is TimeSpan retryAfterDelta)
        {
            return retryAfterDelta;
        }

        if (response?.Headers.TryGetValues("retry-after", out var retryAfterValues) == true
            && int.TryParse(retryAfterValues.FirstOrDefault(), out var retryAfterSeconds))
        {
            return TimeSpan.FromSeconds(Math.Max(0, retryAfterSeconds));
        }

        var multiplier = Math.Pow(2, Math.Max(0, attempt - 1));
        var calculatedDelay = TimeSpan.FromMilliseconds(options.InitialRetryDelay.TotalMilliseconds * multiplier);

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

    private static string? BuildSystemPrompt(IReadOnlyList<ChatMessage> messages)
    {
        var systemParts = messages
            .Where(static message => message.Role == ChatMessageRole.System && !string.IsNullOrWhiteSpace(message.Content))
            .Select(static message => message.Content.Trim())
            .ToArray();

        return systemParts.Length == 0
            ? null
            : string.Join(Environment.NewLine + Environment.NewLine, systemParts);
    }

    private static JsonArray BuildMessages(IReadOnlyList<ChatMessage> messages)
    {
        var anthropicMessages = new JsonArray();

        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];

            if (message.Role == ChatMessageRole.System)
            {
                continue;
            }

            if (message.Role == ChatMessageRole.Tool)
            {
                var toolResultBlocks = new JsonArray();

                while (index < messages.Count && messages[index].Role == ChatMessageRole.Tool)
                {
                    var toolMessage = messages[index];

                    if (string.IsNullOrWhiteSpace(toolMessage.ToolCallId))
                    {
                        throw new InvalidOperationException("Tool messages require a tool call id.");
                    }

                    toolResultBlocks.Add(new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = toolMessage.ToolCallId,
                        ["content"] = toolMessage.Content,
                        ["is_error"] = toolMessage.IsError
                    });

                    index++;
                }

                index--;

                anthropicMessages.Add(new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = toolResultBlocks
                });

                continue;
            }

            anthropicMessages.Add(new JsonObject
            {
                ["role"] = ToAnthropicRole(message.Role),
                ["content"] = BuildContentBlocks(message)
            });
        }

        return anthropicMessages;
    }

    private static JsonArray BuildContentBlocks(ChatMessage message)
    {
        var contentBlocks = new JsonArray();

        if (!string.IsNullOrWhiteSpace(message.Content))
        {
            contentBlocks.Add(new JsonObject
            {
                ["type"] = "text",
                ["text"] = message.Content
            });
        }

        if (message.ToolCalls is null)
        {
            return contentBlocks;
        }

        foreach (var toolCall in message.ToolCalls)
        {
            contentBlocks.Add(new JsonObject
            {
                ["type"] = "tool_use",
                ["id"] = toolCall.Id,
                ["name"] = toolCall.Name,
                ["input"] = toolCall.Arguments?.DeepClone() ?? new JsonObject()
            });
        }

        return contentBlocks;
    }

    private static JsonArray BuildTools(IReadOnlyList<ToolSchema> tools)
    {
        var anthropicTools = new JsonArray();

        foreach (var tool in tools)
        {
            var toolObject = new JsonObject
            {
                ["name"] = tool.Name,
                ["input_schema"] = tool.Parameters?.DeepClone() ?? new JsonObject()
            };

            if (!string.IsNullOrWhiteSpace(tool.Description))
            {
                toolObject["description"] = tool.Description;
            }

            anthropicTools.Add(toolObject);
        }

        return anthropicTools;
    }

    private static InvocationResponse ParseInvocationResponse(JsonElement root)
    {
        var toolCalls = new List<ToolCall>();
        var textParts = new List<string>();

        if (root.TryGetProperty("content", out var contentElement) && contentElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var contentItem in contentElement.EnumerateArray())
            {
                if (!contentItem.TryGetProperty("type", out var typeElement))
                {
                    continue;
                }

                var type = typeElement.GetString();

                if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase)
                    && contentItem.TryGetProperty("text", out var textElement))
                {
                    var text = textElement.GetString();

                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        textParts.Add(text);
                    }

                    continue;
                }

                if (string.Equals(type, "tool_use", StringComparison.OrdinalIgnoreCase))
                {
                    toolCalls.Add(new ToolCall(
                        contentItem.GetProperty("id").GetString()
                            ?? throw new InvalidOperationException("Anthropic tool_use block is missing an id."),
                        contentItem.GetProperty("name").GetString()
                            ?? throw new InvalidOperationException("Anthropic tool_use block is missing a name."),
                        contentItem.TryGetProperty("input", out var inputElement)
                            ? JsonNode.Parse(inputElement.GetRawText())
                            : null));
                }
            }
        }

        var stopReason = ParseStopReason(root);

        var assistantMessage = new ChatMessage(
            ChatMessageRole.Assistant,
            string.Join(Environment.NewLine, textParts.Where(static part => !string.IsNullOrWhiteSpace(part))),
            toolCalls.Count > 0 ? toolCalls : null);

        return new InvocationResponse(
            assistantMessage,
            stopReason,
            ParseTokenUsage(root));
    }

    private static InvocationStopReason ParseStopReason(JsonElement root)
    {
        if (!root.TryGetProperty("stop_reason", out var stopReasonElement))
        {
            return InvocationStopReason.Unknown;
        }

        return stopReasonElement.GetString() switch
        {
            "end_turn" => InvocationStopReason.EndTurn,
            "tool_use" => InvocationStopReason.ToolCalls,
            "max_tokens" => InvocationStopReason.MaxTokens,
            "stop_sequence" => InvocationStopReason.StopSequence,
            _ => InvocationStopReason.Unknown
        };
    }

    private static TokenUsage? ParseTokenUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usageElement) || usageElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var inputTokens = usageElement.TryGetProperty("input_tokens", out var inputTokensElement)
            ? inputTokensElement.GetInt32()
            : 0;
        var outputTokens = usageElement.TryGetProperty("output_tokens", out var outputTokensElement)
            ? outputTokensElement.GetInt32()
            : 0;

        return new TokenUsage(inputTokens, outputTokens, inputTokens + outputTokens);
    }

    private static string ToAnthropicRole(ChatMessageRole role)
    {
        return role switch
        {
            ChatMessageRole.User => "user",
            ChatMessageRole.Assistant => "assistant",
            _ => throw new InvalidOperationException($"Unsupported Anthropic role mapping for '{role}'.")
        };
    }
}
