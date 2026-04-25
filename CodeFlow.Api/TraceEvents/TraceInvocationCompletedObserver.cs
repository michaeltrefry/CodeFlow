using CodeFlow.Contracts;
using MassTransit;
using MassTransit.RabbitMqTransport;

namespace CodeFlow.Api.TraceEvents;

public sealed class TraceInvocationCompletedObserver : IConsumer<AgentInvocationCompleted>
{
    private readonly TraceEventBroker broker;

    public TraceInvocationCompletedObserver(TraceEventBroker broker)
    {
        this.broker = broker ?? throw new ArgumentNullException(nameof(broker));
    }

    public Task Consume(ConsumeContext<AgentInvocationCompleted> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var message = context.Message;
        return broker.PublishAsync(
            new TraceEvent(
                TraceId: message.TraceId,
                RoundId: message.RoundId,
                Kind: TraceEventKind.Completed,
                AgentKey: message.AgentKey,
                AgentVersion: message.AgentVersion,
                OutputRef: message.OutputRef,
                InputRef: null,
                Decision: message.OutputPortName,
                DecisionPayload: message.DecisionPayload,
                TimestampUtc: context.SentTime ?? DateTimeOffset.UtcNow),
            context.CancellationToken);
    }
}

public sealed class TraceInvocationCompletedObserverDefinition : ConsumerDefinition<TraceInvocationCompletedObserver>
{
    public TraceInvocationCompletedObserverDefinition()
    {
        EndpointName = $"api-trace-observer-completion-{Environment.MachineName}-{Guid.NewGuid():N}";
    }

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<TraceInvocationCompletedObserver> consumerConfigurator,
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
