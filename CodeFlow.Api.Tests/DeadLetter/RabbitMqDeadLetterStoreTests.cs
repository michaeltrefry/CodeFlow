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
    public async Task PeekQueueAsync_ShouldUseExplicitMassTransitMessageIdWhenPresent()
    {
        var handler = new RecordingHttpHandler();
        var expectedMessageId = Guid.NewGuid().ToString();

        handler.SetJsonResponse(HttpMethod.Post, "/api/queues/codeflow/agent-invocations_error/get", new JsonArray
        {
            new JsonObject
            {
                ["payload"] = "{\"traceId\":\"abc\"}",
                ["payload_encoding"] = "string",
                ["properties"] = new JsonObject
                {
                    ["message_id"] = expectedMessageId,
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

        messages.Should().ContainSingle();
        messages[0].MessageId.Should().Be(expectedMessageId);
        messages[0].OriginalInputAddress.Should().Be("rabbitmq://localhost/agent-invocations");
    }

    [Fact]
    public async Task PeekQueueAsync_WithoutMessageId_FallsBackToHashIncludingFaultTimestamp()
    {
        // Two messages with identical payload + queue name but different fault timestamps —
        // the legacy payload-only hash would collide; the new fallback must distinguish them.
        var handler = new RecordingHttpHandler();
        var sharedPayload = "{\"agent\":\"reviewer\",\"reason\":\"tool-call-budget\"}";

        handler.SetJsonResponse(HttpMethod.Post, "/api/queues/codeflow/agent-invocations_error/get", new JsonArray
        {
            new JsonObject
            {
                ["payload"] = sharedPayload,
                ["payload_encoding"] = "string",
                ["properties"] = new JsonObject
                {
                    ["headers"] = new JsonObject
                    {
                        ["MT-Fault-Timestamp"] = "2026-04-20T10:00:00Z"
                    }
                }
            },
            new JsonObject
            {
                ["payload"] = sharedPayload,
                ["payload_encoding"] = "string",
                ["properties"] = new JsonObject
                {
                    ["headers"] = new JsonObject
                    {
                        ["MT-Fault-Timestamp"] = "2026-04-20T11:00:00Z"
                    }
                }
            }
        });

        var store = BuildStore(handler);

        var messages = await store.PeekQueueAsync("agent-invocations_error");

        messages.Should().HaveCount(2);
        messages[0].MessageId.Should().NotBe(messages[1].MessageId);
        messages.Select(m => m.MessageId).Should().AllSatisfy(id => id.Should().StartWith("h-"));
    }

    [Fact]
    public async Task RetryAsync_ShouldDelegateToAtomicTransportAndReturnSuccessWithRepublishTarget()
    {
        var handler = new RecordingHttpHandler();
        var messageId = Guid.NewGuid().ToString();

        handler.SetJsonResponse(HttpMethod.Post,
            "/api/queues/codeflow/agent-invocations_error/get",
            new JsonArray
            {
                new JsonObject
                {
                    ["payload"] = "{\"target\":true}",
                    ["payload_encoding"] = "string",
                    ["properties"] = new JsonObject
                    {
                        ["message_id"] = messageId,
                        ["headers"] = new JsonObject
                        {
                            ["MT-Fault-InputAddress"] = "rabbitmq://localhost/agent-invocations"
                        }
                    }
                }
            });

        var transport = new RecordingDlqRetryTransport(
            result: new DlqTransferResult(DlqTransferOutcome.Transferred, 1, null));

        var store = BuildStore(handler, transport);
        var result = await store.RetryAsync("agent-invocations_error", messageId);

        result.Success.Should().BeTrue();
        result.RepublishedTo.Should().Be("agent-invocations");
        transport.Calls.Should().ContainSingle();
        transport.Calls[0].SourceQueue.Should().Be("agent-invocations_error");
        transport.Calls[0].TargetQueue.Should().Be("agent-invocations");
        transport.Calls[0].TargetMessageId.Should().Be(messageId);
    }

    [Fact]
    public async Task RetryAsync_WhenPeekCannotFindMessage_ReturnsFailureWithoutInvokingTransport()
    {
        var handler = new RecordingHttpHandler();
        handler.SetJsonResponse(HttpMethod.Post,
            "/api/queues/codeflow/agent-invocations_error/get",
            new JsonArray());

        var transport = new RecordingDlqRetryTransport(
            result: new DlqTransferResult(DlqTransferOutcome.Transferred, 0, null));

        var store = BuildStore(handler, transport);
        var result = await store.RetryAsync("agent-invocations_error", "missing-id");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
        transport.Calls.Should().BeEmpty("the store must not attempt an atomic transfer when the message is not in the peek window");
    }

    [Fact]
    public async Task RetryAsync_WhenTransportThrows_ReportsFailureAndPreservesSource()
    {
        // The transport throws mid-transfer. The atomicity guarantee lives in the transport
        // implementation (AMQP channel close requeues unacked messages). The store must surface
        // the failure as ErrorMessage and never pretend success.
        var handler = new RecordingHttpHandler();
        var messageId = Guid.NewGuid().ToString();
        handler.SetJsonResponse(HttpMethod.Post,
            "/api/queues/codeflow/agent-invocations_error/get",
            new JsonArray
            {
                new JsonObject
                {
                    ["payload"] = "{\"x\":1}",
                    ["payload_encoding"] = "string",
                    ["properties"] = new JsonObject
                    {
                        ["message_id"] = messageId,
                        ["headers"] = new JsonObject
                        {
                            ["MT-Fault-InputAddress"] = "rabbitmq://localhost/agent-invocations"
                        }
                    }
                }
            });

        var transport = new ThrowingDlqRetryTransport(new InvalidOperationException("AMQP connect refused"));
        var store = BuildStore(handler, transport);

        var result = await store.RetryAsync("agent-invocations_error", messageId);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("AMQP connect refused");
    }

    private static RabbitMqDeadLetterStore BuildStore(
        HttpMessageHandler handler,
        IDlqRetryTransport? transport = null)
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
        return new RabbitMqDeadLetterStore(
            client,
            options,
            transport ?? new RecordingDlqRetryTransport(new DlqTransferResult(DlqTransferOutcome.Transferred, 0, null)),
            NullLogger<RabbitMqDeadLetterStore>.Instance);
    }

    private sealed class RecordingDlqRetryTransport : IDlqRetryTransport
    {
        private readonly DlqTransferResult result;

        public RecordingDlqRetryTransport(DlqTransferResult result)
        {
            this.result = result;
        }

        public List<(string SourceQueue, string TargetQueue, string TargetMessageId)> Calls { get; } = [];

        public Task<DlqTransferResult> TransferAsync(
            string sourceQueue,
            string targetQueue,
            string targetMessageId,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((sourceQueue, targetQueue, targetMessageId));
            return Task.FromResult(result);
        }
    }

    private sealed class ThrowingDlqRetryTransport : IDlqRetryTransport
    {
        private readonly Exception exception;

        public ThrowingDlqRetryTransport(Exception exception)
        {
            this.exception = exception;
        }

        public Task<DlqTransferResult> TransferAsync(
            string sourceQueue,
            string targetQueue,
            string targetMessageId,
            CancellationToken cancellationToken = default)
        {
            throw exception;
        }
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
