using CodeFlow.Contracts;
using MassTransit;

namespace CodeFlow.Api.TraceEvents;

public sealed class TraceInvokeRequestedObserver : IConsumer<AgentInvokeRequested>
{
    private readonly TraceEventBroker broker;

    public TraceInvokeRequestedObserver(TraceEventBroker broker)
    {
        this.broker = broker ?? throw new ArgumentNullException(nameof(broker));
    }

    public Task Consume(ConsumeContext<AgentInvokeRequested> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var message = context.Message;
        return broker.PublishAsync(
            new TraceEvent(
                TraceId: message.TraceId,
                RoundId: message.RoundId,
                Kind: TraceEventKind.Requested,
                AgentKey: message.AgentKey,
                AgentVersion: message.AgentVersion,
                OutputRef: null,
                InputRef: message.InputRef,
                Decision: null,
                DecisionPayload: null,
                TimestampUtc: context.SentTime ?? DateTimeOffset.UtcNow),
            context.CancellationToken);
    }
}

public sealed class TraceInvokeRequestedObserverDefinition : ConsumerDefinition<TraceInvokeRequestedObserver>
{
    public TraceInvokeRequestedObserverDefinition()
    {
        EndpointName = $"api-trace-observer-invoke-{Environment.MachineName}-{Guid.NewGuid():N}";
    }
}
