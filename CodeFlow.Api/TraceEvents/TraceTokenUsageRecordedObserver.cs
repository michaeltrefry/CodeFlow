using CodeFlow.Contracts;
using MassTransit;
using MassTransit.RabbitMqTransport;

namespace CodeFlow.Api.TraceEvents;

/// <summary>
/// Translates <see cref="TokenUsageRecorded"/> contracts onto the in-memory
/// <see cref="TraceEventBroker"/> so SSE subscribers on
/// <c>GET /api/traces/{id}/stream</c> see token usage land in real time. Mirrors the
/// existing <see cref="TraceInvocationCompletedObserver"/> pattern: per-pod, auto-delete,
/// non-durable RabbitMQ queue.
/// </summary>
public sealed class TraceTokenUsageRecordedObserver : IConsumer<TokenUsageRecorded>
{
    private readonly TraceEventBroker broker;

    public TraceTokenUsageRecordedObserver(TraceEventBroker broker)
    {
        this.broker = broker ?? throw new ArgumentNullException(nameof(broker));
    }

    public Task Consume(ConsumeContext<TokenUsageRecorded> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var message = context.Message;
        return broker.PublishAsync(
            new TraceEvent(
                TraceId: message.TraceId,
                // Round-id is not part of the token-usage record (a single LLM round-trip
                // correlates via InvocationId, which is already on the payload). Empty Guid keeps
                // the existing TraceEvent shape consistent for unsupported kinds.
                RoundId: Guid.Empty,
                Kind: TraceEventKind.TokenUsageRecorded,
                AgentKey: string.Empty,
                AgentVersion: 0,
                OutputRef: null,
                InputRef: null,
                Decision: null,
                DecisionPayload: null,
                TimestampUtc: context.SentTime ?? new DateTimeOffset(message.RecordedAtUtc, TimeSpan.Zero),
                TokenUsage: new TokenUsageEventPayload(
                    RecordId: message.RecordId,
                    NodeId: message.NodeId,
                    InvocationId: message.InvocationId,
                    ScopeChain: message.ScopeChain,
                    Provider: message.Provider,
                    Model: message.Model,
                    Usage: message.Usage)),
            context.CancellationToken);
    }
}

public sealed class TraceTokenUsageRecordedObserverDefinition : ConsumerDefinition<TraceTokenUsageRecordedObserver>
{
    public TraceTokenUsageRecordedObserverDefinition()
    {
        EndpointName = $"api-trace-observer-token-usage-{Environment.MachineName}-{Guid.NewGuid():N}";
    }

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<TraceTokenUsageRecordedObserver> consumerConfigurator,
        IRegistrationContext context)
    {
        // Per-instance, in-memory-only broadcast queue: declare as auto-delete and non-durable so
        // each API pod's queue dies with it and RabbitMQ doesn't accumulate orphans across
        // rolling deploys.
        if (endpointConfigurator is IRabbitMqReceiveEndpointConfigurator rabbit)
        {
            rabbit.AutoDelete = true;
            rabbit.Durable = false;
        }
    }
}
