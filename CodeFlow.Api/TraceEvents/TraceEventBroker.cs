using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace CodeFlow.Api.TraceEvents;

public sealed class TraceEventBroker
{
    private readonly ConcurrentDictionary<Guid, Channel<TraceEvent>> subscriptions = new();

    public async Task PublishAsync(TraceEvent traceEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(traceEvent);

        var snapshot = subscriptions.ToArray();
        foreach (var pair in snapshot)
        {
            try
            {
                await pair.Value.Writer.WriteAsync(traceEvent, cancellationToken);
            }
            catch (ChannelClosedException)
            {
                subscriptions.TryRemove(pair.Key, out _);
            }
        }
    }

    public async IAsyncEnumerable<TraceEvent> SubscribeAsync(
        Guid traceId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var subscriptionId = Guid.NewGuid();
        var channel = Channel.CreateUnbounded<TraceEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        subscriptions[subscriptionId] = channel;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TraceEvent traceEvent;
                try
                {
                    traceEvent = await channel.Reader.ReadAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }
                catch (ChannelClosedException)
                {
                    yield break;
                }

                if (traceEvent.TraceId == traceId)
                {
                    yield return traceEvent;
                }
            }
        }
        finally
        {
            subscriptions.TryRemove(subscriptionId, out _);
            channel.Writer.TryComplete();
        }
    }
}
