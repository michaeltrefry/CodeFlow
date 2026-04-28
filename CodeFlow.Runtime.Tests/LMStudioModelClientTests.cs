using FluentAssertions;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using CodeFlow.Runtime.LMStudio;
using CodeFlow.Runtime.OpenAI;

namespace CodeFlow.Runtime.Tests;

public sealed class LMStudioModelClientTests
{
    [Fact]
    public async Task InvokeAsync_ShouldReuseResponsesTranslationAndConfiguredBaseUrl()
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
                          "text": "Paris is the capital of France."
                        }
                      ]
                    },
                    {
                      "id": "fc_123",
                      "call_id": "call_123",
                      "type": "function_call",
                      "name": "search_docs",
                      "arguments": "{\"query\":\"France capital\"}"
                    }
                  ],
                  "usage": {
                    "input_tokens": 11,
                    "output_tokens": 7,
                    "total_tokens": 18
                  }
                }
                """)
            }
        ]);

        using var httpClient = new HttpClient(handler);
        var client = new LMStudioModelClient(
            httpClient,
            new LMStudioModelClientOptions
            {
                ResponsesEndpoint = new Uri("http://localhost:1234/v1/responses"),
                InitialRetryDelay = TimeSpan.Zero
            });

        var response = await client.InvokeAsync(
            new InvocationRequest(
                Messages:
                [
                    new ChatMessage(ChatMessageRole.System, "You are a geography assistant."),
                    new ChatMessage(ChatMessageRole.User, "What is the capital of France?"),
                    new ChatMessage(ChatMessageRole.Tool, "{\"capital\":\"Paris\"}", ToolCallId: "call_123")
                ],
                Tools:
                [
                    new ToolSchema(
                        "search_docs",
                        "Search indexed documents.",
                        new JsonObject())
                ],
                Model: "qwen2.5-7b-instruct"));

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Uri.Should().Be(new Uri("http://localhost:1234/v1/responses"));
        handler.Requests[0].AuthorizationScheme.Should().BeNull();

        var requestJson = JsonNode.Parse(handler.Requests[0].Body)!;
        requestJson["model"]!.GetValue<string>().Should().Be("qwen2.5-7b-instruct");
        requestJson["input"]!.AsArray().Should().HaveCount(3);

        response.StopReason.Should().Be(InvocationStopReason.ToolCalls);
        response.Message.Content.Should().Be("Paris is the capital of France.");
        response.Message.ToolCalls.Should().ContainSingle();
        response.Message.ToolCalls![0].Name.Should().Be("search_docs");
        response.TokenUsage.Should().BeEquivalentTo(new TokenUsage(11, 7, 18));
    }

    [Fact]
    public async Task InvokeAsync_ShouldExposeRawUsageVerbatimFromLmStudioResponse()
    {
        // Slice 4 of the Token Usage Tracking epic. LMStudio rides on
        // OpenAiCompatibleResponsesModelClientBase, so it already inherits the slice 2
        // CloneRawUsage path — but local providers report a slightly different `usage` shape
        // than OpenAI (no `output_tokens_details.reasoning_tokens`, no `cache_*_tokens`,
        // and varying total/sum reporting depending on the runtime). This test pins LMStudio's
        // actual fields against a recorded sample so the capture pipeline produces records with
        // the local-provider shape today, and any future LMStudio surface change is caught
        // explicitly.
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
                        { "type": "output_text", "text": "ok" }
                      ]
                    }
                  ],
                  "usage": {
                    "input_tokens": 42,
                    "output_tokens": 9,
                    "total_tokens": 51,
                    "prompt_tokens_details": { "cached_tokens": 0 },
                    "lmstudio_runtime": "llama.cpp"
                  }
                }
                """)
            }
        ]);

        using var httpClient = new HttpClient(handler);
        var client = new LMStudioModelClient(
            httpClient,
            new LMStudioModelClientOptions
            {
                ResponsesEndpoint = new Uri("http://localhost:1234/v1/responses"),
                InitialRetryDelay = TimeSpan.Zero
            });

        var response = await client.InvokeAsync(
            new InvocationRequest(
                Messages: [new ChatMessage(ChatMessageRole.User, "Hi.")],
                Tools: null,
                Model: "qwen2.5-7b-instruct"));

        response.TokenUsage.Should().BeEquivalentTo(new TokenUsage(42, 9, 51));

        response.RawUsage.Should().NotBeNull();
        var usage = response.RawUsage!.Value;
        usage.GetProperty("input_tokens").GetInt32().Should().Be(42);
        usage.GetProperty("output_tokens").GetInt32().Should().Be(9);
        usage.GetProperty("total_tokens").GetInt32().Should().Be(51);
        usage.GetProperty("prompt_tokens_details").GetProperty("cached_tokens").GetInt32().Should().Be(0);
        usage.GetProperty("lmstudio_runtime").GetString().Should().Be("llama.cpp");
    }

    [Fact]
    public async Task InvokeAsync_WhenLmStudioResponseHasNoUsage_RawUsageIsNull()
    {
        // Some local-runtime configurations omit `usage` entirely. The capture observer's
        // `rawUsage != null` gate must skip cleanly for these calls rather than persisting an
        // empty record.
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
                        { "type": "output_text", "text": "ok" }
                      ]
                    }
                  ]
                }
                """)
            }
        ]);

        using var httpClient = new HttpClient(handler);
        var client = new LMStudioModelClient(
            httpClient,
            new LMStudioModelClientOptions
            {
                ResponsesEndpoint = new Uri("http://localhost:1234/v1/responses"),
                InitialRetryDelay = TimeSpan.Zero
            });

        var response = await client.InvokeAsync(
            new InvocationRequest(
                Messages: [new ChatMessage(ChatMessageRole.User, "Hi.")],
                Tools: null,
                Model: "qwen2.5-7b-instruct"));

        response.TokenUsage.Should().BeNull();
        response.RawUsage.Should().BeNull();
    }

    [Fact]
    public void ModelClientRegistry_ShouldResolveLmStudioProviderSeparatelyFromOpenAi()
    {
        using var openAiHttpClient = new HttpClient(new StubHttpMessageHandler([]));
        using var lmStudioHttpClient = new HttpClient(new StubHttpMessageHandler([]));

        var openAiClient = new OpenAIModelClient(
            openAiHttpClient,
            new OpenAIModelClientOptions { ApiKey = "openai-test-key" });
        var lmStudioClient = new LMStudioModelClient(
            lmStudioHttpClient,
            new LMStudioModelClientOptions());

        var registry = new ModelClientRegistry(
        [
            new ModelClientRegistration("openai", openAiClient),
            new ModelClientRegistration("lmstudio", lmStudioClient)
        ]);

        registry.Resolve("lmstudio").Should().BeSameAs(lmStudioClient);
        registry.Resolve("openai").Should().BeSameAs(openAiClient);
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

            if (responses.Count == 0)
            {
                throw new InvalidOperationException("No stubbed responses were configured.");
            }

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
