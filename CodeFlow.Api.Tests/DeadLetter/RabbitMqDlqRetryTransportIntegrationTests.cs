using CodeFlow.Host.DeadLetter;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using System.Text;
using Testcontainers.RabbitMq;

namespace CodeFlow.Api.Tests.DeadLetter;

public sealed class RabbitMqDlqRetryTransportIntegrationTests : IAsyncLifetime
{
    private readonly RabbitMqContainer container = new RabbitMqBuilder("rabbitmq:4.0-management")
        .WithUsername("codeflow")
        .WithPassword("codeflow_dev")
        .Build();

    public Task InitializeAsync() => container.StartAsync();

    public Task DisposeAsync() => container.DisposeAsync().AsTask();

    [Fact]
    public async Task TransferAsync_MovesMatchingMessage_AndLeavesNonMatchingOnSource()
    {
        const string sourceQueue = "codeflow-test_error";
        const string targetQueue = "codeflow-test";

        var (transport, factory) = BuildTransport();

        await using var setupConnection = await factory.CreateConnectionAsync();
        await using var setupChannel = await setupConnection.CreateChannelAsync();
        await setupChannel.QueueDeclareAsync(sourceQueue, durable: true, exclusive: false, autoDelete: false);
        await setupChannel.QueueDeclareAsync(targetQueue, durable: true, exclusive: false, autoDelete: false);

        var leadingMessageId = Guid.NewGuid().ToString();
        var targetMessageId = Guid.NewGuid().ToString();
        var trailingMessageId = Guid.NewGuid().ToString();

        await PublishAsync(setupChannel, sourceQueue, leadingMessageId, "leading");
        await PublishAsync(setupChannel, sourceQueue, targetMessageId, "target");
        await PublishAsync(setupChannel, sourceQueue, trailingMessageId, "trailing");

        var result = await transport.TransferAsync(sourceQueue, targetQueue, targetMessageId, CancellationToken.None);

        result.Outcome.Should().Be(DlqTransferOutcome.Transferred);

        // Target queue must have exactly the transferred message with its id preserved.
        var targetDelivered = await DrainAsync(factory, targetQueue);
        targetDelivered.Should().ContainSingle();
        targetDelivered[0].messageId.Should().Be(targetMessageId);
        targetDelivered[0].body.Should().Be("target");

        // Source queue must still contain the two non-matching messages — they were inspected but
        // never acked, so channel close requeued them. Order of redelivery is not guaranteed so
        // we assert by set equality.
        var sourceDelivered = await DrainAsync(factory, sourceQueue);
        sourceDelivered.Select(m => m.messageId).Should().BeEquivalentTo(new[] { leadingMessageId, trailingMessageId });
    }

    [Fact]
    public async Task TransferAsync_WhenTargetMissing_ReturnsNotFound_AndSourceIsUnchanged()
    {
        const string sourceQueue = "codeflow-other_error";
        const string targetQueue = "codeflow-other";

        var (transport, factory) = BuildTransport();

        await using var setupConnection = await factory.CreateConnectionAsync();
        await using var setupChannel = await setupConnection.CreateChannelAsync();
        await setupChannel.QueueDeclareAsync(sourceQueue, durable: true, exclusive: false, autoDelete: false);
        await setupChannel.QueueDeclareAsync(targetQueue, durable: true, exclusive: false, autoDelete: false);

        var presentId = Guid.NewGuid().ToString();
        await PublishAsync(setupChannel, sourceQueue, presentId, "only-message");

        var result = await transport.TransferAsync(
            sourceQueue,
            targetQueue,
            targetMessageId: Guid.NewGuid().ToString(),
            CancellationToken.None);

        result.Outcome.Should().Be(DlqTransferOutcome.NotFound);

        (await DrainAsync(factory, targetQueue)).Should().BeEmpty();

        var sourceDelivered = await DrainAsync(factory, sourceQueue);
        sourceDelivered.Should().ContainSingle();
        sourceDelivered[0].messageId.Should().Be(presentId);
    }

    private (RabbitMqDlqRetryTransport Transport, ConnectionFactory Factory) BuildTransport()
    {
        var options = Options.Create(new DeadLetterOptions
        {
            ManagementHost = container.Hostname,
            AmqpHost = container.Hostname,
            AmqpPort = container.GetMappedPublicPort(5672),
            VirtualHost = "/",
            Username = "codeflow",
            Password = "codeflow_dev",
            RetryScanTimeout = TimeSpan.FromSeconds(5),
            MaxPeekPerQueue = 25
        });

        var factory = new ConnectionFactory
        {
            HostName = container.Hostname,
            Port = container.GetMappedPublicPort(5672),
            VirtualHost = "/",
            UserName = "codeflow",
            Password = "codeflow_dev"
        };

        return (new RabbitMqDlqRetryTransport(options, NullLogger<RabbitMqDlqRetryTransport>.Instance), factory);
    }

    private static async Task PublishAsync(IChannel channel, string queue, string messageId, string body)
    {
        var props = new BasicProperties
        {
            MessageId = messageId,
            DeliveryMode = DeliveryModes.Persistent
        };

        await channel.BasicPublishAsync(
            exchange: string.Empty,
            routingKey: queue,
            mandatory: true,
            basicProperties: props,
            body: Encoding.UTF8.GetBytes(body));
    }

    private static async Task<List<(string? messageId, string body)>> DrainAsync(
        ConnectionFactory factory,
        string queue)
    {
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();

        var drained = new List<(string? messageId, string body)>();
        while (true)
        {
            var result = await channel.BasicGetAsync(queue, autoAck: true);
            if (result is null)
            {
                break;
            }

            drained.Add((result.BasicProperties.MessageId, Encoding.UTF8.GetString(result.Body.Span)));
        }

        return drained;
    }
}
