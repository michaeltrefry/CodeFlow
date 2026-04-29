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
    private readonly Func<AnthropicModelClientOptions> optionsResolver;

    public AnthropicModelClient(HttpClient httpClient, Func<AnthropicModelClientOptions> optionsResolver)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.optionsResolver = optionsResolver ?? throw new ArgumentNullException(nameof(optionsResolver));
    }

    // Convenience ctor for tests and for fixed-options call sites.
    public AnthropicModelClient(HttpClient httpClient, AnthropicModelClientOptions options)
        : this(httpClient, () => options ?? throw new ArgumentNullException(nameof(options)))
    {
    }

    public async Task<InvocationResponse> InvokeAsync(
        InvocationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var options = optionsResolver()
            ?? throw new InvalidOperationException("AnthropicModelClientOptions resolver returned null.");
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new InvalidOperationException(
                "An Anthropic API key has not been configured. Set it via the LLM providers settings page or the "
                + "'Anthropic:ApiKey' configuration value before invoking an Anthropic agent.");
        }
        if (string.IsNullOrWhiteSpace(options.ApiVersion))
        {
            throw new InvalidOperationException("An Anthropic API version is required.");
        }

        var toolNameMap = ProviderToolNameMap.Create(request.Tools);
        using var httpRequest = BuildHttpRequest(request, options, toolNameMap);
        using var response = await ChatModelHttpRetry.SendWithRetryAsync(
            httpClient,
            httpRequest,
            maxRetryAttempts: options.MaxRetryAttempts,
            initialRetryDelay: options.InitialRetryDelay,
            extraRetryStatusCheck: AnthropicExtraRetryStatus,
            extraRetryAfterExtractor: TryReadAnthropicRetryAfter,
            cancellationToken);

        await ModelClientHttpErrorHelper.EnsureSuccessStatusCodeAsync(httpRequest, response, cancellationToken);

        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(contentStream, cancellationToken: cancellationToken);

        return ParseInvocationResponse(document.RootElement, toolNameMap);
    }

    private static HttpRequestMessage BuildHttpRequest(
        InvocationRequest request,
        AnthropicModelClientOptions options,
        ProviderToolNameMap toolNameMap)
    {
        var payload = new JsonObject
        {
            ["model"] = request.Model,
            ["max_tokens"] = request.MaxTokens ?? 1024,
            ["messages"] = BuildMessages(request.Messages, toolNameMap)
        };

        // Prompt caching: the system prompt and the tools catalog are identical across every
        // round of a single agent invocation. Marking each with cache_control=ephemeral lets
        // Anthropic serve rounds 2+ from cache instead of re-billing and re-latency-hitting the
        // full prefix each time. Safe to do unconditionally on Claude >= 3.5.
        var systemPrompt = BuildSystemPrompt(request.Messages);
        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            payload["system"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = systemPrompt,
                    ["cache_control"] = new JsonObject { ["type"] = "ephemeral" },
                },
            };
        }

        if (request.Tools is { Count: > 0 })
        {
            var toolsJson = BuildTools(request.Tools, toolNameMap);
            // Anthropic's cache rule: the cache_control marker on the last tool caches the whole
            // tools array prefix — no need to mark every entry.
            if (toolsJson.Count > 0 && toolsJson[^1] is JsonObject lastTool)
            {
                lastTool["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
            }
            payload["tools"] = toolsJson;
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

    /// <summary>
    /// Anthropic returns 529 ("Overloaded") in addition to the standard retryable set;
    /// the shared <see cref="ChatModelHttpRetry"/> helper accepts that via this hook.
    /// </summary>
    private static bool AnthropicExtraRetryStatus(HttpStatusCode statusCode) =>
        (int)statusCode == 529;

    /// <summary>
    /// Anthropic occasionally surfaces <c>retry-after</c> as a lower-cased header that
    /// <see cref="System.Net.Http.Headers.HttpResponseHeaders.RetryAfter"/> doesn't pick up.
    /// Read it directly so callers don't lose the server-supplied delay.
    /// </summary>
    private static TimeSpan? TryReadAnthropicRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("retry-after", out var values)
            && int.TryParse(values.FirstOrDefault(), out var seconds))
        {
            return TimeSpan.FromSeconds(Math.Max(0, seconds));
        }
        return null;
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

    private static JsonArray BuildMessages(IReadOnlyList<ChatMessage> messages, ProviderToolNameMap toolNameMap)
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
                ["content"] = BuildContentBlocks(message, toolNameMap)
            });
        }

        return anthropicMessages;
    }

    private static JsonArray BuildContentBlocks(ChatMessage message, ProviderToolNameMap toolNameMap)
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
                ["name"] = toolNameMap.ToProviderName(toolCall.Name),
                ["input"] = toolCall.Arguments?.DeepClone() ?? new JsonObject()
            });
        }

        return contentBlocks;
    }

    private static JsonArray BuildTools(IReadOnlyList<ToolSchema> tools, ProviderToolNameMap toolNameMap)
    {
        var anthropicTools = new JsonArray();

        foreach (var tool in tools)
        {
            var toolObject = new JsonObject
            {
                ["name"] = toolNameMap.ToProviderName(tool.Name),
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

    private static InvocationResponse ParseInvocationResponse(JsonElement root, ProviderToolNameMap toolNameMap)
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
                        toolNameMap.ToInternalName(
                            contentItem.GetProperty("name").GetString()
                                ?? throw new InvalidOperationException("Anthropic tool_use block is missing a name.")),
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
            ParseTokenUsage(root),
            CloneRawUsage(root));
    }

    /// <summary>
    /// Clone Anthropic's <c>usage</c> object verbatim so the orchestration-side capture observer
    /// can persist every reported field — input_tokens, output_tokens, cache_creation_input_tokens,
    /// cache_read_input_tokens, extended-thinking detail blocks, and any future field the SDK
    /// surfaces. Returns null when the response has no usage object at all (capture is skipped).
    /// </summary>
    private static JsonElement? CloneRawUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usageElement) || usageElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        return usageElement.Clone();
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
