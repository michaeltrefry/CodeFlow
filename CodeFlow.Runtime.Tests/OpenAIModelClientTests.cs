using FluentAssertions;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using CodeFlow.Runtime.OpenAI;

namespace CodeFlow.Runtime.Tests;

public sealed class OpenAIModelClientTests
{
    [Fact]
    public async Task InvokeAsync_ShouldTranslateMessagesToolsAndRetryTransientFailures()
    {
        var handler = new StubHttpMessageHandler(
        [
            _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Headers =
                {
                    RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero)
                }
            },
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent("""
                {
                  "status": "completed",
                  "output": [
                    {
                      "type": "message",
                      "role": "assistant",
                      "content": [
                        {
                          "type": "output_text",
                          "text": "Paris is the capital of France."
                        }
                      ]
                    }
                  ],
                  "usage": {
                    "input_tokens": 12,
                    "output_tokens": 8,
                    "total_tokens": 20
                  }
                }
                """)
            }
        ]);

        using var httpClient = new HttpClient(handler);
        var client = new OpenAIModelClient(
            httpClient,
            new OpenAIModelClientOptions
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
                        string.Empty,
                        ToolCalls:
                        [
                            new ToolCall(
                                "call_123",
                                "search_docs",
                                new JsonObject { ["query"] = "France capital" })
                        ]),
                    new ChatMessage(ChatMessageRole.Tool, "{\"capital\":\"Paris\"}", ToolCallId: "call_123")
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
                Model: "gpt-5",
                MaxTokens: 200,
                Temperature: 0.1));

        handler.Requests.Should().HaveCount(2);
        response.Message.Content.Should().Be("Paris is the capital of France.");

        var requestJson = JsonNode.Parse(handler.Requests[1].Body)!;
        requestJson["model"]!.GetValue<string>().Should().Be("gpt-5");
        requestJson["store"]!.GetValue<bool>().Should().BeFalse();
        requestJson["max_output_tokens"]!.GetValue<int>().Should().Be(200);
        requestJson["temperature"]!.GetValue<double>().Should().Be(0.1);
        requestJson["tools"]!.AsArray().Should().HaveCount(1);

        var inputItems = requestJson["input"]!.AsArray();
        inputItems.Should().HaveCount(4);
        inputItems[0]!["type"]!.GetValue<string>().Should().Be("message");
        inputItems[0]!["role"]!.GetValue<string>().Should().Be("system");
        inputItems[1]!["role"]!.GetValue<string>().Should().Be("user");
        inputItems[2]!["type"]!.GetValue<string>().Should().Be("function_call");
        inputItems[2]!["call_id"]!.GetValue<string>().Should().Be("call_123");
        inputItems[2]!["arguments"]!.GetValue<string>().Should().Contain("France capital");
        inputItems[3]!["type"]!.GetValue<string>().Should().Be("function_call_output");
        inputItems[3]!["call_id"]!.GetValue<string>().Should().Be("call_123");

        handler.Requests.Should().OnlyContain(request =>
            request.AuthorizationScheme == "Bearer" && request.AuthorizationParameter == "test-key");
    }

    [Fact]
    public async Task InvokeAsync_ShouldTranslateFunctionCallsFromResponsesOutput()
    {
        var handler = new StubHttpMessageHandler(
        [
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent("""
                {
                  "status": "completed",
                  "output": [
                    {
                      "type": "message",
                      "role": "assistant",
                      "content": [
                        {
                          "type": "output_text",
                          "text": "Let me check that."
                        }
                      ]
                    },
                    {
                      "id": "fc_abc",
                      "call_id": "call_abc",
                      "type": "function_call",
                      "name": "get_weather",
                      "arguments": "{\"location\":\"Paris\"}"
                    }
                  ],
                  "usage": {
                    "input_tokens": 20,
                    "output_tokens": 6,
                    "total_tokens": 26
                  }
                }
                """)
            }
        ]);

        using var httpClient = new HttpClient(handler);
        var client = new OpenAIModelClient(httpClient, new OpenAIModelClientOptions { ApiKey = "test-key" });

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
                Model: "gpt-5"));

        response.StopReason.Should().Be(InvocationStopReason.ToolCalls);
        response.Message.Content.Should().Be("Let me check that.");
        response.Message.ToolCalls.Should().ContainSingle();
        response.Message.ToolCalls![0].Id.Should().Be("call_abc");
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
                  "status": "completed",
                  "output": [
                    {
                      "id": "fc_alias",
                      "call_id": "call_alias",
                      "type": "function_call",
                      "name": "{{providerToolName}}",
                      "arguments": "{\"name\":\"TraceCreated\"}"
                    }
                  ],
                  "usage": {
                    "input_tokens": 20,
                    "output_tokens": 6,
                    "total_tokens": 26
                  }
                }
                """)
            }
        ]);

        using var httpClient = new HttpClient(handler);
        var client = new OpenAIModelClient(httpClient, new OpenAIModelClientOptions { ApiKey = "test-key" });

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
                                "call_prev",
                                internalToolName,
                                new JsonObject { ["name"] = "WorkflowStarted" })
                        ]),
                    new ChatMessage(ChatMessageRole.Tool, "{\"consumers\":[]}", ToolCallId: "call_prev")
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
                Model: "gpt-5"));

        var requestJson = JsonNode.Parse(handler.Requests.Single().Body)!;
        requestJson["tools"]!.AsArray()[0]!["name"]!.GetValue<string>().Should().Be(providerToolName);
        requestJson["input"]!.AsArray()[1]!["type"]!.GetValue<string>().Should().Be("function_call");
        requestJson["input"]!.AsArray()[1]!["name"]!.GetValue<string>().Should().Be(providerToolName);

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
                  "error": {
                    "type": "invalid_request_error",
                    "message": "Missing required field."
                  }
                }
                """)
            }
        ]);

        using var httpClient = new HttpClient(handler);
        var client = new OpenAIModelClient(
            httpClient,
            new OpenAIModelClientOptions
            {
                ApiKey = "test-key",
                InitialRetryDelay = TimeSpan.Zero
            });

        Func<Task> act = async () => await client.InvokeAsync(
            new InvocationRequest(
                Messages: [new ChatMessage(ChatMessageRole.User, "Write a PRD")],
                Tools: null,
                Model: "gpt-5"));

        var exception = await act.Should().ThrowAsync<ModelClientHttpException>();
        exception.Which.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        exception.Which.Message.Should().Contain("400 (Bad Request)");
        exception.Which.ProviderErrorMessage.Should().Be("Missing required field.");
        exception.Which.RequestUri.Should().Be(new Uri("https://api.openai.com/v1/responses"));
        exception.Which.RequestHeaders["Authorization"].Should().ContainSingle("[REDACTED]");
        exception.Which.RequestBody.Should().Contain("\"model\":\"gpt-5\"");
        exception.Which.ResponseHeaders["Content-Type"].Should().ContainSingle(header => header.Contains("application/json"));
        exception.Which.ResponseBody.Should().Contain("Missing required field.");
        exception.Which.Message.Should().Contain("Request: ");
        exception.Which.Message.Should().Contain("\"model\":\"gpt-5\"");
        exception.Which.Message.Should().Contain("\"content\":\"Write a PRD\"");
        exception.Which.Message.Should().Contain("Response: ");
        exception.Which.Message.Should().Contain("invalid_request_error");
        exception.Which.Message.Should().Contain("Missing required field.");
        exception.Which.Message.Should().NotContain("test-key");
    }

    [Fact]
    public async Task InvokeAsync_WhenErrorRequestBodyIsLarge_ShouldPreserveBothStartAndEndOfRequest()
    {
        var handler = new StubHttpMessageHandler(
        [
            _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = JsonContent("""{"error":{"message":"bad request"}}""")
            }
        ]);

        using var httpClient = new HttpClient(handler);
        var client = new OpenAIModelClient(
            httpClient,
            new OpenAIModelClientOptions
            {
                ApiKey = "test-key",
                InitialRetryDelay = TimeSpan.Zero
            });

        var largePrompt = new string('A', 140_000) + "TAIL-MARKER-123";

        Func<Task> act = async () => await client.InvokeAsync(
            new InvocationRequest(
                Messages: [new ChatMessage(ChatMessageRole.User, largePrompt)],
                Tools: null,
                Model: "gpt-5"));

        var exception = await act.Should().ThrowAsync<ModelClientHttpException>();
        exception.Which.ProviderErrorMessage.Should().Be("bad request");
        exception.Which.Message.Should().Contain("Request: ");
        exception.Which.Message.Should().Contain("\"model\":\"gpt-5\"");
        exception.Which.Message.Should().Contain("TAIL-MARKER-123");
        exception.Which.Message.Should().Contain("(truncated ");
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
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter,
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
        string? AuthorizationScheme,
        string? AuthorizationParameter,
        string Body);
}
