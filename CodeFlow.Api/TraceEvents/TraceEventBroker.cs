using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeFlow.Api.TraceEvents;

public sealed class TraceEventBroker
{
    // Events are partitioned by TraceId so a publish only fans out to subscribers watching that
    // specific trace — avoids O(subscribers) broadcast cost per event. Each subscription gets its
    // own bounded channel so a slow client can't grow memory without bound; `DropOldest` keeps
    // the stream live by shedding the oldest pending event rather than blocking publishers.
    private const int SubscriberChannelCapacity = 256;

    private readonly ConcurrentDictionary<Guid, SubscriberSet> subscribersByTrace = new();
    private readonly ILogger<TraceEventBroker> logger;

    public TraceEventBroker(ILogger<TraceEventBroker>? logger = null)
    {
        this.logger = logger ?? NullLogger<TraceEventBroker>.Instance;
    }

    public Task PublishAsync(TraceEvent traceEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(traceEvent);

        if (!subscribersByTrace.TryGetValue(traceEvent.TraceId, out var set))
        {
            return Task.CompletedTask;
        }

        foreach (var (subscriptionId, channel) in set.Snapshot())
        {
            // Bounded + DropOldest: TryWrite never blocks or throws on a full buffer; it drops the
            // oldest pending event and takes the new one. If the channel is already closed, evict
            // the dead subscription.
            if (channel.Writer.TryWrite(traceEvent))
            {
                continue;
            }

            if (!set.TryRemove(subscriptionId))
            {
                continue;
            }

            channel.Writer.TryComplete();
            logger.LogDebug(
                "Evicted dead SSE subscription {SubscriptionId} for trace {TraceId}.",
                subscriptionId,
                traceEvent.TraceId);
        }

        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<TraceEvent> SubscribeAsync(
        Guid traceId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var subscriptionId = Guid.NewGuid();
        var channel = Channel.CreateBounded<TraceEvent>(new BoundedChannelOptions(SubscriberChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

        var set = subscribersByTrace.GetOrAdd(traceId, static _ => new SubscriberSet());
        set.Add(subscriptionId, channel);

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

                yield return traceEvent;
            }
        }
        finally
        {
            set.TryRemove(subscriptionId);
            channel.Writer.TryComplete();

            // Clean up the per-trace set when the last subscriber unsubscribes so the dictionary
            // doesn't accumulate empty entries for long-gone traces.
            if (set.IsEmpty)
            {
                subscribersByTrace.TryRemove(new KeyValuePair<Guid, SubscriberSet>(traceId, set));
            }
        }
    }

    private sealed class SubscriberSet
    {
        private readonly ConcurrentDictionary<Guid, Channel<TraceEvent>> channels = new();

        public bool IsEmpty => channels.IsEmpty;

        public void Add(Guid subscriptionId, Channel<TraceEvent> channel)
        {
            channels[subscriptionId] = channel;
        }

        public bool TryRemove(Guid subscriptionId)
        {
            return channels.TryRemove(subscriptionId, out _);
        }

        public IEnumerable<KeyValuePair<Guid, Channel<TraceEvent>>> Snapshot()
        {
            return channels.ToArray();
        }
    }
}
