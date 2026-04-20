using CodeFlow.Host.DeadLetter;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace CodeFlow.Api.Tests.DeadLetter;

public sealed class RabbitMqDeadLetterStoreTests
{
    [Fact]
    public async Task ListQueuesAsync_ShouldReturnOnlyErrorQueues()
    {
        var handler = new RecordingHttpHandler();
        handler.SetJsonResponse(HttpMethod.Get, "/api/queues/codeflow", new JsonArray
        {
            new JsonObject { ["name"] = "agent-invocations", ["messages"] = 4 },
            new JsonObject { ["name"] = "agent-invocations_error", ["messages"] = 2 },
            new JsonObject { ["name"] = "workflow-saga_error", ["messages"] = 1 }
        });

        var store = BuildStore(handler);

        var queues = await store.ListQueuesAsync();

        queues.Select(q => q.QueueName).Should().BeEquivalentTo(
            new[] { "agent-invocations_error", "workflow-saga_error" });
        queues.Single(q => q.QueueName == "agent-invocations_error").MessageCount.Should().Be(2);
    }

    [Fact]
    public async Task PeekQueueAsync_ShouldMapMassTransitFaultHeaders()
    {
        var handler = new RecordingHttpHandler();
        handler.SetJsonResponse(HttpMethod.Post, "/api/queues/codeflow/agent-invocations_error/get", new JsonArray
        {
            new JsonObject
            {
                ["payload"] = "{\"traceId\":\"abc\"}",
                ["payload_encoding"] = "string",
                ["properties"] = new JsonObject
                {
                    ["headers"] = new JsonObject
                    {
                        ["MT-Fault-InputAddress"] = "rabbitmq://localhost/agent-invocations",
                        ["MT-Fault-Message"] = "Budget exceeded",
                        ["MT-Fault-ExceptionType"] = "System.Exception",
                        ["MT-Fault-Timestamp"] = "2026-04-20T16:30:00Z"
                    }
                }
            }
        });

        var store = BuildStore(handler);

        var messages = await store.PeekQueueAsync("agent-invocations_error");

        messages.Should().HaveCount(1);
        var message = messages[0];
        message.QueueName.Should().Be("agent-invocations_error");
        message.OriginalInputAddress.Should().Be("rabbitmq://localhost/agent-invocations");
        message.FaultExceptionMessage.Should().Be("Budget exceeded");
        message.FaultExceptionType.Should().Be("System.Exception");
        message.FirstFaultedAtUtc.Should().NotBeNull();
        message.MessageId.Should().NotBeNullOrWhiteSpace();
        message.PayloadPreview.Should().Contain("traceId");
    }

    [Fact]
    public async Task RetryAsync_ShouldRepublishToOriginalInputQueue()
    {
        var handler = new RecordingHttpHandler();

        // Peek phase — store first computes the id by peeking (non-destructive), then Retry issues consuming gets.
        var targetPayload = "{\"target\":true}";
        var peekResponse = new JsonArray
        {
            new JsonObject
            {
                ["payload"] = targetPayload,
                ["payload_encoding"] = "string",
                ["properties"] = new JsonObject
                {
                    ["headers"] = new JsonObject
                    {
                        ["MT-Fault-InputAddress"] = "rabbitmq://localhost/agent-invocations"
                    }
                }
            }
        };

        // First call is Peek (non-destructive), second call is Retry (consuming) — both hit the same URL.
        handler.SetJsonResponse(HttpMethod.Post,
            "/api/queues/codeflow/agent-invocations_error/get",
            peekResponse);
        handler.SetJsonResponse(HttpMethod.Post,
            "/api/queues/codeflow/agent-invocations_error/get",
            peekResponse);

        handler.SetJsonResponse(HttpMethod.Post,
            "/api/exchanges/codeflow/amq.default/publish",
            new JsonObject { ["routed"] = true });

        var store = BuildStore(handler);
        var peeked = (await store.PeekQueueAsync("agent-invocations_error")).Single();

        var result = await store.RetryAsync("agent-invocations_error", peeked.MessageId);

        result.Success.Should().BeTrue();
        result.RepublishedTo.Should().Be("agent-invocations");
    }

    private static RabbitMqDeadLetterStore BuildStore(HttpMessageHandler handler)
    {
        var options = Options.Create(new DeadLetterOptions
        {
            ManagementHost = "test-rabbit",
            ManagementPort = 15672,
            VirtualHost = "codeflow",
            Username = "codeflow",
            Password = "codeflow_dev"
        });

        var client = new HttpClient(handler);
        return new RabbitMqDeadLetterStore(client, options, NullLogger<RabbitMqDeadLetterStore>.Instance);
    }

    private sealed class RecordingHttpHandler : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, Queue<HttpResponseMessage>> responses = new();

        public List<HttpRequestMessage> Requests { get; } = [];

        public void SetJsonResponse(HttpMethod method, string pathAndQuery, JsonNode body)
        {
            var key = Key(method, pathAndQuery);
            var queue = responses.GetOrAdd(key, _ => new Queue<HttpResponseMessage>());
            queue.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(body)
            });
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var path = request.RequestUri!.AbsolutePath;
            var key = Key(request.Method, path);

            if (responses.TryGetValue(key, out var queue) && queue.Count > 0)
            {
                return Task.FromResult(queue.Dequeue());
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static string Key(HttpMethod method, string path)
        {
            return $"{method} {path}";
        }
    }
}
