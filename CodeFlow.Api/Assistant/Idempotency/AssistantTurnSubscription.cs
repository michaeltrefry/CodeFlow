using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace CodeFlow.Api.Assistant.Idempotency;

/// <summary>
/// One captured SSE frame from an in-flight assistant turn — the unit of replay and live-tail.
/// Payload is the SSE event body as JSON (already serialised by the producer); event is the
/// SSE event name.
/// </summary>
public sealed record RecordedFrame(string Event, string Payload);

/// <summary>
/// sc-803 — Read-side handle on a live (or just-flushed) assistant turn recorder. A retry
/// dispatch returning <c>LiveTail</c> hands the endpoint one of these so it can replay the
/// snapshot frames the original endpoint already wrote, then live-tail the rest until the
/// producer terminates.
/// </summary>
public interface IAssistantTurnSubscription : IAsyncDisposable
{
    /// <summary>Frames emitted before this subscription was created. Always non-null.</summary>
    IReadOnlyList<RecordedFrame> Snapshot { get; }

    /// <summary>
    /// True when the producer has already flushed by the time this subscription was created.
    /// In that case <see cref="ReadAllAsync"/> yields nothing and the snapshot is everything.
    /// </summary>
    bool Completed { get; }

    /// <summary>
    /// Frames emitted after this subscription was created. Completes (without error) when the
    /// producer terminates normally; faults with <see cref="InvalidOperationException"/> if
    /// the subscriber fell behind a bounded buffer, or with <see cref="TimeoutException"/>
    /// if its lifetime ceiling expired before the producer finished — callers should treat
    /// either as "fall back to terminal replay."
    /// </summary>
    IAsyncEnumerable<RecordedFrame> ReadAllAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Producer-side surface — the recorder implements this so the subscription registry can
/// hand out per-record subscriptions without the registry knowing the recorder type.
/// </summary>
internal interface IAssistantTurnPublisher
{
    /// <summary>
    /// Atomically captures the current snapshot and either (a) attaches a new bounded sink
    /// that will receive every subsequent frame until the producer flushes, or (b) — if the
    /// producer has already flushed — returns a snapshot-only subscription with
    /// <see cref="IAssistantTurnSubscription.Completed"/> = true.
    /// </summary>
    IAssistantTurnSubscription Subscribe(int capacity, TimeSpan lifetime);
}

/// <summary>
/// Per-subscriber bounded channel. Producer never blocks: <see cref="TryPublishOrDrop"/>
/// uses non-blocking <see cref="ChannelWriter{T}.TryWrite"/>, and a slow subscriber whose
/// channel is full has its writer completed with an error so the consumer sees a clean
/// <see cref="ChannelClosedException"/> rather than silently missing frames.
/// </summary>
internal sealed class SubscriberSink : IDisposable
{
    private readonly Channel<RecordedFrame> channel;
    private readonly CancellationTokenSource lifetimeCts;
    private readonly CancellationTokenRegistration lifetimeRegistration;

    public SubscriberSink(int capacity, TimeSpan lifetime, TimeProvider timeProvider)
    {
        channel = Channel.CreateBounded<RecordedFrame>(new BoundedChannelOptions(capacity)
        {
            // FullMode = Wait makes TryWrite return false on full so we can detect a slow
            // subscriber and drop it explicitly. DropWrite would silently lose frames and
            // the subscriber would never know it fell behind.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        lifetimeCts = new CancellationTokenSource(lifetime, timeProvider);
        lifetimeRegistration = lifetimeCts.Token.Register(static state =>
        {
            var sink = (SubscriberSink)state!;
            sink.channel.Writer.TryComplete(
                new TimeoutException("Live-tail subscriber lifetime expired before producer terminated."));
        }, this);
    }

    public ChannelReader<RecordedFrame> Reader => channel.Reader;

    /// <summary>Producer-side write. False return means the sink is dead (full or closed).</summary>
    public bool TryPublishOrDrop(RecordedFrame frame)
    {
        if (channel.Writer.TryWrite(frame))
        {
            return true;
        }

        channel.Writer.TryComplete(
            new InvalidOperationException("Live-tail subscriber fell behind the producer."));
        return false;
    }

    /// <summary>Clean producer-side completion when the producer flushes normally.</summary>
    public void Complete()
    {
        channel.Writer.TryComplete();
    }

    /// <summary>Consumer-side cancel — the subscription was disposed before the producer flushed.</summary>
    public void Cancel()
    {
        channel.Writer.TryComplete();
    }

    public void Dispose()
    {
        lifetimeRegistration.Dispose();
        lifetimeCts.Cancel();
        lifetimeCts.Dispose();
    }
}

/// <summary>
/// Subscription returned while the producer is still publishing. Live frames stream from a
/// bounded channel; disposal removes the sink from the publisher so a disconnect doesn't
/// leak buffer capacity for the rest of the turn.
/// </summary>
internal sealed class LiveSubscription : IAssistantTurnSubscription
{
    private readonly SubscriberSink sink;
    private readonly Action<SubscriberSink> onDispose;
    private bool disposed;

    public LiveSubscription(
        IReadOnlyList<RecordedFrame> snapshot,
        SubscriberSink sink,
        Action<SubscriberSink> onDispose)
    {
        Snapshot = snapshot;
        this.sink = sink;
        this.onDispose = onDispose;
    }

    public IReadOnlyList<RecordedFrame> Snapshot { get; }

    public bool Completed => false;

    public IAsyncEnumerable<RecordedFrame> ReadAllAsync(CancellationToken cancellationToken) =>
        sink.Reader.ReadAllAsync(cancellationToken);

    public ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return ValueTask.CompletedTask;
        }

        disposed = true;
        onDispose(sink);
        sink.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Subscription returned when the producer had already flushed at the moment of subscription.
/// The full event stream is in <see cref="Snapshot"/>; the live channel yields nothing.
/// </summary>
internal sealed class SnapshotSubscription : IAssistantTurnSubscription
{
    public SnapshotSubscription(IReadOnlyList<RecordedFrame> snapshot, bool completed)
    {
        Snapshot = snapshot;
        Completed = completed;
    }

    public IReadOnlyList<RecordedFrame> Snapshot { get; }

    public bool Completed { get; }

#pragma warning disable CS1998 // Empty async iterator is intentional — yield break alone returns IEnumerable.
    public async IAsyncEnumerable<RecordedFrame> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield break;
    }
#pragma warning restore CS1998

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
