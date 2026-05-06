using System.Threading.Channels;
using CodeFlow.Api.Assistant.Idempotency;
using CodeFlow.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace CodeFlow.Api.Tests.Assistant;

/// <summary>
/// sc-803 — Multicast assistant turn recorder tests. The recorder must (a) capture every
/// frame in an in-memory snapshot for replay, (b) fan each frame out to active subscribers,
/// (c) attach a late subscriber atomically so the snapshot + live channel together cover
/// every frame, and (d) drop a slow subscriber without disturbing the producer.
/// </summary>
public sealed class AssistantTurnRecorderSubscriptionTests
{
    private static readonly TimeSpan SubscriberLifetime = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Subscriber_attached_before_first_frame_sees_every_frame()
    {
        var fixture = NewFixture();
        var recorder = fixture.NewRecorder();

        var subscription = fixture.SubscriptionRegistry.TrySubscribe(recorder.RecordId);
        subscription.Should().NotBeNull();
        subscription!.Snapshot.Should().BeEmpty();
        subscription.Completed.Should().BeFalse();

        var collected = new List<RecordedFrame>();
        var collectTask = Task.Run(async () =>
        {
            await foreach (var frame in subscription.ReadAllAsync(CancellationToken.None))
            {
                collected.Add(frame);
            }
        });

        recorder.Record("text-delta", """{"delta":"a"}""");
        recorder.Record("text-delta", """{"delta":"b"}""");
        recorder.Record("text-delta", """{"delta":"c"}""");
        await recorder.FlushAsync(AssistantTurnIdempotencyStatus.Completed, CancellationToken.None);

        await collectTask.WaitAsync(TimeSpan.FromSeconds(2));
        collected.Should().HaveCount(3);
        collected.Select(f => f.Event).Should().AllBe("text-delta");
        collected.Select(f => f.Payload).Should().BeEquivalentTo(
            """{"delta":"a"}""",
            """{"delta":"b"}""",
            """{"delta":"c"}""");

        await subscription.DisposeAsync();
    }

    [Fact]
    public async Task Subscriber_attached_mid_stream_sees_snapshot_plus_subsequent_frames()
    {
        var fixture = NewFixture();
        var recorder = fixture.NewRecorder();

        recorder.Record("a", """{"i":1}""");
        recorder.Record("b", """{"i":2}""");

        // Subscribe halfway through.
        var subscription = fixture.SubscriptionRegistry.TrySubscribe(recorder.RecordId);
        subscription.Should().NotBeNull();
        subscription!.Snapshot.Select(f => f.Event).Should().Equal("a", "b");
        subscription.Completed.Should().BeFalse();

        var live = new List<RecordedFrame>();
        var collectTask = Task.Run(async () =>
        {
            await foreach (var frame in subscription.ReadAllAsync(CancellationToken.None))
            {
                live.Add(frame);
            }
        });

        recorder.Record("c", """{"i":3}""");
        recorder.Record("d", """{"i":4}""");
        await recorder.FlushAsync(AssistantTurnIdempotencyStatus.Completed, CancellationToken.None);

        await collectTask.WaitAsync(TimeSpan.FromSeconds(2));
        live.Select(f => f.Event).Should().Equal("c", "d");

        // Snapshot + live together cover every produced frame in order.
        subscription.Snapshot.Concat(live).Select(f => f.Event)
            .Should().Equal("a", "b", "c", "d");

        await subscription.DisposeAsync();
    }

    [Fact]
    public async Task Subscriber_attached_after_flush_sees_full_snapshot_with_completed_true()
    {
        var fixture = NewFixture();
        var recorder = fixture.NewRecorder();

        recorder.Record("a", "{}");
        recorder.Record("b", "{}");
        await recorder.FlushAsync(AssistantTurnIdempotencyStatus.Completed, CancellationToken.None);

        // Once Flush completes the recorder unregisters; a retry that arrives after that
        // gets null from the registry and falls back to terminal Replay (which the dispatch
        // path covers separately). We exercise the in-between window directly via the
        // publisher to confirm the flushed-Subscribe contract.
        var subscription = ((IAssistantTurnPublisher)recorder)
            .Subscribe(capacity: 16, lifetime: SubscriberLifetime);
        subscription.Completed.Should().BeTrue();
        subscription.Snapshot.Select(f => f.Event).Should().Equal("a", "b");

        // ReadAllAsync should yield nothing — everything is already in Snapshot.
        var live = new List<RecordedFrame>();
        await foreach (var frame in subscription.ReadAllAsync(CancellationToken.None))
        {
            live.Add(frame);
        }
        live.Should().BeEmpty();

        await subscription.DisposeAsync();
    }

    [Fact]
    public async Task Slow_subscriber_is_dropped_without_disturbing_producer_or_other_subscribers()
    {
        var fixture = NewFixture();
        var recorder = fixture.NewRecorder();

        // Tiny capacity so we can blow past it deterministically.
        const int capacity = 4;
        var slow = ((IAssistantTurnPublisher)recorder)
            .Subscribe(capacity: capacity, lifetime: SubscriberLifetime);
        var healthy = ((IAssistantTurnPublisher)recorder)
            .Subscribe(capacity: 1024, lifetime: SubscriberLifetime);

        // Start a healthy reader; deliberately leave the slow subscription unread.
        var healthyFrames = new List<RecordedFrame>();
        var healthyReader = Task.Run(async () =>
        {
            await foreach (var frame in healthy.ReadAllAsync(CancellationToken.None))
            {
                healthyFrames.Add(frame);
            }
        });

        const int total = capacity * 4 + 5;
        for (var i = 0; i < total; i++)
        {
            recorder.Record("frame", $"{{\"i\":{i}}}");
        }
        await recorder.FlushAsync(AssistantTurnIdempotencyStatus.Completed, CancellationToken.None);

        // Healthy subscriber receives every frame.
        await healthyReader.WaitAsync(TimeSpan.FromSeconds(2));
        healthyFrames.Should().HaveCount(total);

        // Slow subscriber's read faults with the explicit "fell behind" InvalidOperationException
        // — its writer was completed-with-error the moment the bounded buffer filled. The
        // bounded frames already in the buffer are still readable; the fault surfaces only
        // once the consumer drains past them.
        var slowDrain = async () =>
        {
            var consumed = 0;
            await foreach (var _ in slow.ReadAllAsync(CancellationToken.None))
            {
                consumed++;
            }
            return consumed;
        };
        (await slowDrain.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("fell behind");

        await slow.DisposeAsync();
        await healthy.DisposeAsync();
    }

    [Fact]
    public async Task Multiple_concurrent_subscribers_all_see_the_full_sequence()
    {
        var fixture = NewFixture();
        var recorder = fixture.NewRecorder();

        const int subscriberCount = 5;
        var subscriptions = Enumerable
            .Range(0, subscriberCount)
            .Select(_ => fixture.SubscriptionRegistry.TrySubscribe(recorder.RecordId)!)
            .ToArray();
        subscriptions.Should().AllSatisfy(s => s.Should().NotBeNull());

        var collected = subscriptions.Select(_ => new List<RecordedFrame>()).ToArray();
        var readers = subscriptions
            .Select((sub, idx) => Task.Run(async () =>
            {
                await foreach (var frame in sub.ReadAllAsync(CancellationToken.None))
                {
                    collected[idx].Add(frame);
                }
            }))
            .ToArray();

        const int total = 32;
        for (var i = 0; i < total; i++)
        {
            recorder.Record("frame", $"{{\"i\":{i}}}");
        }
        await recorder.FlushAsync(AssistantTurnIdempotencyStatus.Completed, CancellationToken.None);

        await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(2));

        foreach (var frames in collected)
        {
            frames.Should().HaveCount(total);
            frames.Select(f => f.Event).Should().AllBe("frame");
            frames.Select((f, idx) => f.Payload == $"{{\"i\":{idx}}}").Should().AllBeEquivalentTo(true);
        }

        foreach (var subscription in subscriptions)
        {
            await subscription.DisposeAsync();
        }
    }

    [Fact]
    public async Task Subscriber_lifetime_expiry_closes_channel_with_timeout_when_producer_never_flushes()
    {
        var timeProvider = new FakeTimeProvider();
        var fixture = NewFixture(timeProvider);
        var recorder = fixture.NewRecorder();

        // Hand-roll a subscription with a short lifetime so we can advance virtual time past it.
        var subscription = ((IAssistantTurnPublisher)recorder)
            .Subscribe(capacity: 16, lifetime: TimeSpan.FromSeconds(2));

        var drain = async () =>
        {
            await foreach (var _ in subscription.ReadAllAsync(CancellationToken.None))
            {
                // never receive a frame — producer stays silent.
            }
        };
        var drainTask = Task.Run(drain);

        timeProvider.Advance(TimeSpan.FromSeconds(3));

        var act = async () => await drainTask.WaitAsync(TimeSpan.FromSeconds(2));
        (await act.Should().ThrowAsync<TimeoutException>())
            .Which.Message.Should().Contain("lifetime expired");

        await subscription.DisposeAsync();
    }

    [Fact]
    public async Task TrySubscribe_returns_null_after_recorder_unregisters_on_flush()
    {
        var fixture = NewFixture();
        var recorder = fixture.NewRecorder();

        recorder.Record("a", "{}");
        await recorder.FlushAsync(AssistantTurnIdempotencyStatus.Completed, CancellationToken.None);

        // Post-flush the registry no longer routes new subscribers — dispatcher will fall
        // back to terminal Replay through the persistence path.
        fixture.SubscriptionRegistry.TrySubscribe(recorder.RecordId).Should().BeNull();
    }

    private static Fixture NewFixture(TimeProvider? timeProvider = null) =>
        new(timeProvider ?? TimeProvider.System);

    private sealed class Fixture
    {
        public Fixture(TimeProvider timeProvider)
        {
            TimeProvider = timeProvider;
            Repository = new InMemoryAssistantTurnIdempotencyRepository();
            SignalRegistry = new AssistantTurnSignalRegistry();
            var options = Options.Create(new AssistantTurnIdempotencyOptions
            {
                LiveTailSubscriberCapacity = 256,
                LiveTailSubscriberLifetime = SubscriberLifetime,
            });
            SubscriptionRegistry = new AssistantTurnSubscriptionRegistry(options);
        }

        public TimeProvider TimeProvider { get; }
        public InMemoryAssistantTurnIdempotencyRepository Repository { get; }
        public AssistantTurnSignalRegistry SignalRegistry { get; }
        public AssistantTurnSubscriptionRegistry SubscriptionRegistry { get; }

        public BufferedAssistantTurnRecorder NewRecorder() => new(
            Guid.NewGuid(),
            Repository,
            SignalRegistry,
            SubscriptionRegistry,
            TimeProvider);
    }

    /// <summary>
    /// In-memory stand-in that satisfies the recorder's <c>MarkTerminalAsync</c> dependency
    /// without spinning up EF. Other repository methods aren't exercised here.
    /// </summary>
    private sealed class InMemoryAssistantTurnIdempotencyRepository : IAssistantTurnIdempotencyRepository
    {
        public List<(Guid Id, AssistantTurnIdempotencyStatus Status, string EventsJson)> Terminals { get; } = new();

        public Task<AssistantTurnClaimOutcome> TryClaimAsync(
            Guid conversationId,
            string idempotencyKey,
            string userId,
            string requestHash,
            DateTime nowUtc,
            TimeSpan ttl,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<AssistantTurnIdempotencyRecord?> GetAsync(
            Guid conversationId,
            string idempotencyKey,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<AssistantTurnIdempotencyRecord?> GetByIdAsync(
            Guid id,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task MarkTerminalAsync(
            Guid id,
            AssistantTurnIdempotencyStatus terminalStatus,
            string eventsJson,
            DateTime completedAtUtc,
            CancellationToken cancellationToken = default)
        {
            Terminals.Add((id, terminalStatus, eventsJson));
            return Task.CompletedTask;
        }

        public Task<int> PurgeExpiredAsync(DateTime nowUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }

    /// <summary>
    /// Minimal <see cref="TimeProvider"/> we can advance under test. Drives the
    /// <c>CancellationTokenSource(TimeSpan, TimeProvider)</c> timer used for subscriber
    /// lifetime expiry.
    /// </summary>
    private sealed class FakeTimeProvider : TimeProvider
    {
        private long ticks;
        private readonly object gate = new();
        private readonly List<FakeTimer> timers = new();

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        public override DateTimeOffset GetUtcNow() => new(ticks, TimeSpan.Zero);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new FakeTimer(this, callback, state, dueTime, period);
            lock (gate)
            {
                timers.Add(timer);
            }
            return timer;
        }

        public void Advance(TimeSpan delta)
        {
            Interlocked.Add(ref ticks, delta.Ticks);
            FakeTimer[] snapshot;
            lock (gate)
            {
                snapshot = timers.ToArray();
            }
            foreach (var timer in snapshot)
            {
                timer.Advance(delta);
            }
        }

        private sealed class FakeTimer : ITimer
        {
            private readonly FakeTimeProvider provider;
            private readonly TimerCallback callback;
            private readonly object? state;
            private TimeSpan remaining;
            private TimeSpan period;
            private bool fired;

            public FakeTimer(FakeTimeProvider provider, TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            {
                this.provider = provider;
                this.callback = callback;
                this.state = state;
                remaining = dueTime;
                this.period = period;
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                remaining = dueTime;
                this.period = period;
                fired = false;
                return true;
            }

            public void Advance(TimeSpan delta)
            {
                if (fired || remaining == Timeout.InfiniteTimeSpan)
                {
                    return;
                }
                remaining -= delta;
                if (remaining <= TimeSpan.Zero)
                {
                    fired = true;
                    callback(state);
                }
            }

            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
