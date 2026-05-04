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

    /// <summary>
    /// Eagerly registers a subscription so events published from this point forward land in the
    /// returned channel. Callers should invoke this BEFORE doing any setup work that runs in
    /// parallel with potential publishes — e.g., the SSE endpoint registers before reading
    /// existing decisions from the database, so a decision committed during that read can't
    /// slip through the gap between "registration" and "iteration starts." Dispose the handle
    /// (or just let the cancellation token fire) to drop the subscription.
    /// </summary>
    public TraceEventSubscription Subscribe(Guid traceId)
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

        return new TraceEventSubscription(traceId, subscriptionId, channel, this);
    }

    public async IAsyncEnumerable<TraceEvent> SubscribeAsync(
        Guid traceId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var subscription = Subscribe(traceId);
        await foreach (var traceEvent in subscription.ReadAllAsync(cancellationToken))
        {
            yield return traceEvent;
        }
    }

    private void RemoveSubscription(Guid traceId, Guid subscriptionId, Channel<TraceEvent> channel)
    {
        if (subscribersByTrace.TryGetValue(traceId, out var set))
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

    public sealed class TraceEventSubscription : IDisposable
    {
        private readonly Guid traceId;
        private readonly Guid subscriptionId;
        private readonly Channel<TraceEvent> channel;
        private readonly TraceEventBroker broker;
        private bool disposed;

        internal TraceEventSubscription(Guid traceId, Guid subscriptionId, Channel<TraceEvent> channel, TraceEventBroker broker)
        {
            this.traceId = traceId;
            this.subscriptionId = subscriptionId;
            this.channel = channel;
            this.broker = broker;
        }

        public async IAsyncEnumerable<TraceEvent> ReadAllAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
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

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            broker.RemoveSubscription(traceId, subscriptionId, channel);
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
