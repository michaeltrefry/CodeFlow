using FluentAssertions;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using CodeFlow.Runtime.Anthropic;

namespace CodeFlow.Runtime.Tests;

public sealed class AnthropicModelClientTests
{
    [Fact]
    public async Task InvokeAsync_ShouldTranslateMessagesToolsAndRetryTransientFailures()
    {
        var handler = new StubHttpMessageHandler(
        [
            _ => new HttpResponseMessage((HttpStatusCode)529)
            {
                Headers =
                {
                    { "retry-after", "0" }
                }
            },
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent("""
                {
                  "id": "msg_123",
                  "type": "message",
                  "role": "assistant",
                  "content": [
                    {
                      "type": "text",
                      "text": "Paris is the capital of France."
                    }
                  ],
                  "stop_reason": "end_turn",
                  "usage": {
                    "input_tokens": 18,
                    "output_tokens": 7
                  }
                }
                """)
            }
        ]);

        using var httpClient = new HttpClient(handler);
        var client = new AnthropicModelClient(
            httpClient,
            new AnthropicModelClientOptions
            {
                ApiKey = "test-key",
                InitialRetryDelay = TimeSpan.Zero
            });

        var response = await client.InvokeAsync(
            new InvocationRequest(
                Messages:
                [
                    new ChatMessage(ChatMessageRole.System, "You are a geography assistant."),
                    new ChatMessage(ChatMessageRole.User, "What is the capital of France?"),
                    new ChatMessage(
                        ChatMessageRole.Assistant,
                        "Let me check.",
                        ToolCalls:
                        [
                            new ToolCall(
                                "toolu_123",
                                "search_docs",
                                new JsonObject { ["query"] = "France capital" })
                        ]),
                    new ChatMessage(ChatMessageRole.Tool, "{\"capital\":\"Paris\"}", ToolCallId: "toolu_123")
                ],
                Tools:
                [
                    new ToolSchema(
                        "search_docs",
                        "Search indexed documents.",
                        new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["query"] = new JsonObject
                                {
                                    ["type"] = "string"
                                }
                            }
                        })
                ],
                Model: "claude-sonnet-4-20250514",
                MaxTokens: 200,
                Temperature: 0.1));

        handler.Requests.Should().HaveCount(2);
        response.Message.Content.Should().Be("Paris is the capital of France.");
        response.StopReason.Should().Be(InvocationStopReason.EndTurn);

        var requestJson = JsonNode.Parse(handler.Requests[1].Body)!;
        requestJson["model"]!.GetValue<string>().Should().Be("claude-sonnet-4-20250514");
        // System prompt is now shaped as a cache_control-marked text block for Anthropic prompt
        // caching, so drill into the first (and only) content block.
        var systemBlock = requestJson["system"]!.AsArray()[0]!;
        systemBlock["type"]!.GetValue<string>().Should().Be("text");
        systemBlock["text"]!.GetValue<string>().Should().Be("You are a geography assistant.");
        systemBlock["cache_control"]!["type"]!.GetValue<string>().Should().Be("ephemeral");
        requestJson["max_tokens"]!.GetValue<int>().Should().Be(200);
        requestJson["temperature"]!.GetValue<double>().Should().Be(0.1);
        requestJson["tools"]!.AsArray().Should().HaveCount(1);
        requestJson["tools"]![0]!["input_schema"]!["properties"]!["query"]!["type"]!.GetValue<string>().Should().Be("string");

        var messages = requestJson["messages"]!.AsArray();
        messages.Should().HaveCount(3);
        messages[0]!["role"]!.GetValue<string>().Should().Be("user");
        messages[1]!["role"]!.GetValue<string>().Should().Be("assistant");
        messages[1]!["content"]!.AsArray().Should().HaveCount(2);
        messages[1]!["content"]![0]!["type"]!.GetValue<string>().Should().Be("text");
        messages[1]!["content"]![1]!["type"]!.GetValue<string>().Should().Be("tool_use");
        messages[1]!["content"]![1]!["id"]!.GetValue<string>().Should().Be("toolu_123");
        messages[2]!["role"]!.GetValue<string>().Should().Be("user");
        messages[2]!["content"]!.AsArray()[0]!["type"]!.GetValue<string>().Should().Be("tool_result");
        messages[2]!["content"]!.AsArray()[0]!["tool_use_id"]!.GetValue<string>().Should().Be("toolu_123");

        handler.Requests.Should().OnlyContain(request =>
            request.ApiKey == "test-key" && request.ApiVersion == "2023-06-01");
    }

    [Fact]
    public async Task InvokeAsync_ShouldTranslateToolUseBlocksFromAnthropicResponse()
    {
        var handler = new StubHttpMessageHandler(
        [
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent("""
                {
                  "id": "msg_456",
                  "type": "message",
                  "role": "assistant",
                  "content": [
                    {
                      "type": "text",
                      "text": "I'll check that."
                    },
                    {
                      "type": "tool_use",
                      "id": "toolu_abc",
                      "name": "get_weather",
                      "input": {
                        "location": "Paris"
                      }
                    }
                  ],
                  "stop_reason": "tool_use",
                  "usage": {
                    "input_tokens": 20,
                    "output_tokens": 6
                  }
                }
                """)
            }
        ]);

        using var httpClient = new HttpClient(handler);
        var client = new AnthropicModelClient(httpClient, new AnthropicModelClientOptions { ApiKey = "test-key" });

        var response = await client.InvokeAsync(
            new InvocationRequest(
                Messages: [new ChatMessage(ChatMessageRole.User, "What is the weather in Paris?")],
                Tools:
                [
                    new ToolSchema(
                        "get_weather",
                        "Gets weather by location.",
                        new JsonObject())
                ],
                Model: "claude-sonnet-4-20250514"));

        response.StopReason.Should().Be(InvocationStopReason.ToolCalls);
        response.Message.Content.Should().Be("I'll check that.");
        response.Message.ToolCalls.Should().ContainSingle();
        response.Message.ToolCalls![0].Id.Should().Be("toolu_abc");
        response.Message.ToolCalls![0].Name.Should().Be("get_weather");
        response.Message.ToolCalls![0].Arguments!["location"]!.GetValue<string>().Should().Be("Paris");
        response.TokenUsage.Should().BeEquivalentTo(new TokenUsage(20, 6, 26));
    }

    [Fact]
    public async Task InvokeAsync_ShouldAliasProviderToolNamesAndMapThemBackToInternalNames()
    {
        const string internalToolName = "mcp:codegraph:find_consumers";
        const string providerToolName = "mcp_codegraph_find_consumers";

        var handler = new StubHttpMessageHandler(
        [
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent($$"""
                {
                  "id": "msg_alias",
                  "type": "message",
                  "role": "assistant",
                  "content": [
                    {
                      "type": "tool_use",
                      "id": "toolu_alias",
                      "name": "{{providerToolName}}",
                      "input": {
                        "name": "TraceCreated"
                      }
                    }
                  ],
                  "stop_reason": "tool_use",
                  "usage": {
                    "input_tokens": 20,
                    "output_tokens": 6
                  }
                }
                """)
            }
        ]);

        using var httpClient = new HttpClient(handler);
        var client = new AnthropicModelClient(httpClient, new AnthropicModelClientOptions { ApiKey = "test-key" });

        var response = await client.InvokeAsync(
            new InvocationRequest(
                Messages:
                [
                    new ChatMessage(ChatMessageRole.User, "Find consumers."),
                    new ChatMessage(
                        ChatMessageRole.Assistant,
                        string.Empty,
                        ToolCalls:
                        [
                            new ToolCall(
                                "toolu_prev",
                                internalToolName,
                                new JsonObject { ["name"] = "WorkflowStarted" })
                        ]),
                    new ChatMessage(ChatMessageRole.Tool, "{\"consumers\":[]}", ToolCallId: "toolu_prev")
                ],
                Tools:
                [
                    new ToolSchema(
                        internalToolName,
                        "Find all consumers for an event.",
                        new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["name"] = new JsonObject
                                {
                                    ["type"] = "string"
                                }
                            }
                        })
                ],
                Model: "claude-sonnet-4-20250514"));

        var requestJson = JsonNode.Parse(handler.Requests.Single().Body)!;
        requestJson["tools"]!.AsArray()[0]!["name"]!.GetValue<string>().Should().Be(providerToolName);
        requestJson["messages"]!.AsArray()[1]!["content"]!.AsArray()[0]!["type"]!.GetValue<string>().Should().Be("tool_use");
        requestJson["messages"]!.AsArray()[1]!["content"]!.AsArray()[0]!["name"]!.GetValue<string>().Should().Be(providerToolName);

        response.Message.ToolCalls.Should().ContainSingle();
        response.Message.ToolCalls![0].Name.Should().Be(internalToolName);
        response.Message.ToolCalls[0].Arguments!["name"]!.GetValue<string>().Should().Be("TraceCreated");
    }

    [Fact]
    public async Task InvokeAsync_WhenProviderReturnsBadRequest_ShouldIncludeRequestAndResponseBodiesInException()
    {
        var handler = new StubHttpMessageHandler(
        [
            _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = JsonContent("""
                {
                  "type": "error",
                  "error": {
                    "type": "invalid_request_error",
                    "message": "messages: field is required"
                  }
                }
                """)
            }
        ]);

        using var httpClient = new HttpClient(handler);
        var client = new AnthropicModelClient(
            httpClient,
            new AnthropicModelClientOptions
            {
                ApiKey = "test-key",
                InitialRetryDelay = TimeSpan.Zero
            });

        Func<Task> act = async () => await client.InvokeAsync(
            new InvocationRequest(
                Messages: [new ChatMessage(ChatMessageRole.User, "Write a PRD")],
                Tools: null,
                Model: "claude-sonnet-4-20250514"));

        var exception = await act.Should().ThrowAsync<ModelClientHttpException>();
        exception.Which.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        exception.Which.Message.Should().Contain("400 (Bad Request)");
        exception.Which.ProviderErrorMessage.Should().Be("messages: field is required");
        exception.Which.RequestUri.Should().Be(new Uri("https://api.anthropic.com/v1/messages"));
        exception.Which.RequestHeaders["x-api-key"].Should().ContainSingle("[REDACTED]");
        exception.Which.RequestBody.Should().Contain("\"model\":\"claude-sonnet-4-20250514\"");
        exception.Which.ResponseBody.Should().Contain("messages: field is required");
        exception.Which.Message.Should().Contain("Request: ");
        exception.Which.Message.Should().Contain("\"model\":\"claude-sonnet-4-20250514\"");
        exception.Which.Message.Should().Contain("\"text\":\"Write a PRD\"");
        exception.Which.Message.Should().Contain("Response: ");
        exception.Which.Message.Should().Contain("invalid_request_error");
        exception.Which.Message.Should().Contain("messages: field is required");
        exception.Which.Message.Should().NotContain("test-key");
    }

    [Fact]
    public async Task InvokeAsync_ShouldExposeRawUsageVerbatimIncludingCacheAndThinkingAndUnknownFields()
    {
        // Slice 3 of the Token Usage Tracking epic. Anthropic surfaces several usage fields that
        // the schema-flat `TokenUsage` drops — cache_creation_input_tokens, cache_read_input_tokens,
        // and (when extended thinking is enabled) cache_creation detail blocks plus per-call
        // thinking budget fields. The model client must clone `usage` verbatim so the
        // orchestration-side capture observer can persist every field, including ones we don't
        // know about yet.
        var handler = new StubHttpMessageHandler(
        [
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent("""
                {
                  "id": "msg_usage",
                  "type": "message",
                  "role": "assistant",
                  "content": [
                    { "type": "text", "text": "ok" }
                  ],
                  "stop_reason": "end_turn",
                  "usage": {
                    "input_tokens": 100,
                    "output_tokens": 50,
                    "cache_creation_input_tokens": 25,
                    "cache_read_input_tokens": 12,
                    "cache_creation": { "ephemeral_5m_input_tokens": 25, "ephemeral_1h_input_tokens": 0 },
                    "server_tool_use": { "web_search_requests": 0 },
                    "some_future_field": "future_value"
                  }
                }
                """)
            }
        ]);

        using var httpClient = new HttpClient(handler);
        var client = new AnthropicModelClient(httpClient, new AnthropicModelClientOptions { ApiKey = "test-key" });

        var response = await client.InvokeAsync(
            new InvocationRequest(
                Messages: [new ChatMessage(ChatMessageRole.User, "Hi.")],
                Tools: null,
                Model: "claude-sonnet-4-20250514"));

        // Flat TokenUsage stays as the existing 3-int shape — that's the live-UI surface.
        response.TokenUsage.Should().BeEquivalentTo(new TokenUsage(100, 50, 150));

        // RawUsage is the new surface and the only one that retains the full provider payload.
        response.RawUsage.Should().NotBeNull();
        var usage = response.RawUsage!.Value;
        usage.GetProperty("input_tokens").GetInt32().Should().Be(100);
        usage.GetProperty("output_tokens").GetInt32().Should().Be(50);
        usage.GetProperty("cache_creation_input_tokens").GetInt32().Should().Be(25);
        usage.GetProperty("cache_read_input_tokens").GetInt32().Should().Be(12);
        usage.GetProperty("cache_creation").GetProperty("ephemeral_5m_input_tokens").GetInt32().Should().Be(25);
        usage.GetProperty("server_tool_use").GetProperty("web_search_requests").GetInt32().Should().Be(0);
        usage.GetProperty("some_future_field").GetString().Should().Be("future_value");
    }

    [Fact]
    public async Task InvokeAsync_WhenResponseHasNoUsageObject_RawUsageIsNull()
    {
        // Edge case mirroring the OpenAI client: a response that omits `usage` entirely must
        // surface RawUsage = null so the capture observer's `rawUsage != null` gate skips
        // cleanly (rather than persisting an empty object).
        var handler = new StubHttpMessageHandler(
        [
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent("""
                {
                  "id": "msg_no_usage",
                  "type": "message",
                  "role": "assistant",
                  "content": [
                    { "type": "text", "text": "ok" }
                  ],
                  "stop_reason": "end_turn"
                }
                """)
            }
        ]);

        using var httpClient = new HttpClient(handler);
        var client = new AnthropicModelClient(httpClient, new AnthropicModelClientOptions { ApiKey = "test-key" });

        var response = await client.InvokeAsync(
            new InvocationRequest(
                Messages: [new ChatMessage(ChatMessageRole.User, "Hi.")],
                Tools: null,
                Model: "claude-sonnet-4-20250514"));

        response.TokenUsage.Should().BeNull();
        response.RawUsage.Should().BeNull();
    }

    private static StringContent JsonContent(string json)
    {
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    private sealed class StubHttpMessageHandler(IReadOnlyList<Func<HttpRequestMessage, HttpResponseMessage>> responses) : HttpMessageHandler
    {
        private int nextResponseIndex;

        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            Requests.Add(new CapturedRequest(
                request.RequestUri,
                request.Headers.TryGetValues("x-api-key", out var apiKeyValues) ? apiKeyValues.FirstOrDefault() : null,
                request.Headers.TryGetValues("anthropic-version", out var apiVersionValues) ? apiVersionValues.FirstOrDefault() : null,
                body));

            if (nextResponseIndex >= responses.Count)
            {
                throw new InvalidOperationException("No more stubbed responses were configured.");
            }

            return responses[nextResponseIndex++](request);
        }
    }

    private sealed record CapturedRequest(
        Uri? Uri,
        string? ApiKey,
        string? ApiVersion,
        string Body);
}
