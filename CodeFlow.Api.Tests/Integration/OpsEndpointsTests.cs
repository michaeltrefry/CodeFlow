using CodeFlow.Api.Dtos;
using CodeFlow.Host.DeadLetter;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;

namespace CodeFlow.Api.Tests.Integration;

[Trait("Category", "EndToEnd")]
public sealed class OpsEndpointsTests : IClassFixture<CodeFlowApiFactory>
{
    private readonly CodeFlowApiFactory factory;

    public OpsEndpointsTests(CodeFlowApiFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task GetDlq_ReturnsQueuesAndMessagesFromStore()
    {
        var fake = new FakeDeadLetterStore
        {
            Queues =
            [
                new DeadLetterQueueSummary("agent-invocations_error", 2),
                new DeadLetterQueueSummary("workflow-saga_error", 0)
            ],
            Messages =
            [
                new DeadLetterMessage(
                    MessageId: "abc123",
                    QueueName: "agent-invocations_error",
                    OriginalInputAddress: "rabbitmq://localhost/agent-invocations",
                    FaultExceptionMessage: "Tool call budget exceeded",
                    FaultExceptionType: "System.Exception",
                    FirstFaultedAtUtc: DateTimeOffset.UtcNow,
                    PayloadPreview: "{\"trace\":\"abc\"}",
                    Headers: new Dictionary<string, string>())
            ]
        };

        using var client = CreateClientWithStore(fake);

        var response = await client.GetFromJsonAsync<DeadLetterListResponse>("/api/ops/dlq");

        response.Should().NotBeNull();
        response!.Queues.Should().HaveCount(2);
        response.Messages.Should().ContainSingle()
            .Which.FaultExceptionMessage.Should().Be("Tool call budget exceeded");
    }

    [Fact]
    public async Task RetryDlq_CallsStoreAndReturnsResult()
    {
        var fake = new FakeDeadLetterStore
        {
            RetryResult = new DeadLetterRetryResult(true, "agent-invocations", null)
        };

        using var client = CreateClientWithStore(fake);

        var response = await client.PostAsync(
            "/api/ops/dlq/agent-invocations_error/retry/abc123",
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<DeadLetterRetryResponse>();
        body!.Success.Should().BeTrue();
        body.RepublishedTo.Should().Be("agent-invocations");

        fake.RetryCalls.Should().ContainSingle()
            .Which.Should().Be(("agent-invocations_error", "abc123"));
    }

    [Fact]
    public async Task Metrics_EmitsPrometheusGauge()
    {
        var fake = new FakeDeadLetterStore
        {
            Queues =
            [
                new DeadLetterQueueSummary("agent-invocations_error", 3)
            ]
        };

        using var client = CreateClientWithStore(fake);

        var body = await client.GetStringAsync("/api/ops/metrics");

        body.Should().Contain("# TYPE codeflow_dlq_messages gauge");
        body.Should().Contain("codeflow_dlq_messages{queue=\"agent-invocations_error\"} 3");
    }

    private HttpClient CreateClientWithStore(IDeadLetterStore store)
    {
        return factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                var existing = services
                    .Where(s => s.ServiceType == typeof(IDeadLetterStore))
                    .ToList();
                foreach (var descriptor in existing)
                {
                    services.Remove(descriptor);
                }

                services.AddSingleton(store);
            });
        }).CreateClient();
    }

    private sealed class FakeDeadLetterStore : IDeadLetterStore
    {
        public IReadOnlyList<DeadLetterQueueSummary> Queues { get; set; } = Array.Empty<DeadLetterQueueSummary>();

        public IReadOnlyList<DeadLetterMessage> Messages { get; set; } = Array.Empty<DeadLetterMessage>();

        public DeadLetterRetryResult RetryResult { get; set; } = new(false, null, null);

        public List<(string Queue, string Message)> RetryCalls { get; } = [];

        public Task<IReadOnlyList<DeadLetterQueueSummary>> ListQueuesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Queues);

        public Task<IReadOnlyList<DeadLetterMessage>> ListMessagesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Messages);

        public Task<IReadOnlyList<DeadLetterMessage>> PeekQueueAsync(string queueName, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DeadLetterMessage>>(Messages
                .Where(m => m.QueueName == queueName)
                .ToList());

        public Task<DeadLetterRetryResult> RetryAsync(string queueName, string messageId, CancellationToken cancellationToken = default)
        {
            RetryCalls.Add((queueName, messageId));
            return Task.FromResult(RetryResult);
        }
    }
}
