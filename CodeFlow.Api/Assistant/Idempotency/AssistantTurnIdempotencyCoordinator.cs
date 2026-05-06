using System.Text.Json;
using CodeFlow.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeFlow.Api.Assistant.Idempotency;

public sealed class AssistantTurnIdempotencyCoordinator : IAssistantTurnIdempotencyCoordinator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IAssistantTurnIdempotencyRepository repository;
    private readonly AssistantTurnSignalRegistry signalRegistry;
    private readonly AssistantTurnSubscriptionRegistry subscriptionRegistry;
    private readonly IOptions<AssistantTurnIdempotencyOptions> options;
    private readonly ILogger<AssistantTurnIdempotencyCoordinator> logger;
    private readonly TimeProvider timeProvider;

    public AssistantTurnIdempotencyCoordinator(
        IAssistantTurnIdempotencyRepository repository,
        AssistantTurnSignalRegistry signalRegistry,
        AssistantTurnSubscriptionRegistry subscriptionRegistry,
        IOptions<AssistantTurnIdempotencyOptions> options,
        ILogger<AssistantTurnIdempotencyCoordinator> logger,
        TimeProvider timeProvider)
    {
        this.repository = repository;
        this.signalRegistry = signalRegistry;
        this.subscriptionRegistry = subscriptionRegistry;
        this.options = options;
        this.logger = logger;
        this.timeProvider = timeProvider;
    }

    public async Task<AssistantTurnDispatchOutcome> DispatchAsync(
        Guid conversationId,
        string idempotencyKey,
        string userId,
        string requestHash,
        CancellationToken cancellationToken)
    {
        var ttl = options.Value.RecordTtl;
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;

        var outcome = await repository.TryClaimAsync(
            conversationId,
            idempotencyKey,
            userId,
            requestHash,
            nowUtc,
            ttl,
            cancellationToken);

        switch (outcome)
        {
            case AssistantTurnClaimOutcome.Claimed claimed:
                var recorder = new BufferedAssistantTurnRecorder(
                    claimed.Record.Id,
                    repository,
                    signalRegistry,
                    subscriptionRegistry,
                    timeProvider);
                return new AssistantTurnDispatchOutcome.Claimed(claimed.Record, recorder);

            case AssistantTurnClaimOutcome.Existing existing:
                if (!string.Equals(existing.Record.UserId, userId, StringComparison.Ordinal))
                {
                    // Different caller reusing someone else's key — surface as not-found upstream
                    // so we don't confirm the row's existence to an unauthorized caller.
                    return new AssistantTurnDispatchOutcome.UserMismatch(existing.Record);
                }

                if (!string.Equals(existing.Record.RequestHash, requestHash, StringComparison.Ordinal))
                {
                    return new AssistantTurnDispatchOutcome.HashMismatch(existing.Record);
                }

                return existing.Record.Status == AssistantTurnIdempotencyStatus.InFlight
                    ? new AssistantTurnDispatchOutcome.WaitThenReplay(existing.Record)
                    : new AssistantTurnDispatchOutcome.Replay(existing.Record);

            default:
                throw new InvalidOperationException($"Unknown claim outcome: {outcome}");
        }
    }

    public async Task<AssistantTurnIdempotencyRecord> WaitForTerminalAsync(
        Guid recordId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = timeProvider.GetUtcNow() + timeout;
        var pollInterval = options.Value.WaitPollInterval;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var current = await repository.GetByIdAsync(recordId, cancellationToken);
            if (current is null)
            {
                throw new InvalidOperationException(
                    $"Idempotency record '{recordId}' disappeared while waiting (likely TTL-swept).");
            }

            if (current.Status != AssistantTurnIdempotencyStatus.InFlight)
            {
                return current;
            }

            if (timeProvider.GetUtcNow() >= deadline)
            {
                logger.LogWarning(
                    "Timed out waiting for in-flight assistant idempotency record {RecordId} to reach terminal status.",
                    recordId);
                return current;
            }

            using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            pollCts.CancelAfter(pollInterval);
            try
            {
                // Same-instance completion races to wake us before the poll tick fires; if we're
                // listening from a different instance, the signal never arrives and the poll
                // tick is the timeout that drives the next DB read.
                await signalRegistry.WaitAsync(recordId, pollCts.Token);
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                // Poll tick — fall through and re-read.
            }
        }
    }

    public async Task ReplayAsync(
        AssistantTurnIdempotencyRecord record,
        Func<string, string, CancellationToken, Task> writeFrame,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writeFrame);

        var events = DeserializeEvents(record.EventsJson, record.Id);
        foreach (var (name, payload) in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await writeFrame(name, payload, cancellationToken);
        }
    }

    private (string Event, string Payload)[] DeserializeEvents(string eventsJson, Guid recordId)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<RecordedFrame[]>(eventsJson, JsonOptions);
            if (parsed is null)
            {
                return Array.Empty<(string, string)>();
            }

            return parsed
                .Where(f => !string.IsNullOrEmpty(f.Event))
                .Select(f => (f.Event, f.Payload ?? "{}"))
                .ToArray();
        }
        catch (JsonException ex)
        {
            logger.LogError(
                ex,
                "Failed to deserialize idempotency event stream for record {RecordId}; replaying empty.",
                recordId);
            return Array.Empty<(string, string)>();
        }
    }
}

/// <summary>
/// sc-803 — Multicast assistant turn recorder. Captures every <see cref="Record"/> call into
/// an in-memory snapshot and fans the same frame out to any active subscribers (the
/// originating endpoint plus any retries that attached via the
/// <see cref="AssistantTurnSubscriptionRegistry"/>). On <see cref="FlushAsync"/> it persists
/// the full stream and closes every live subscriber cleanly.
/// </summary>
internal sealed class BufferedAssistantTurnRecorder : IAssistantTurnRecorder, IAssistantTurnPublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly object lockObj = new();
    private readonly List<RecordedFrame> snapshot = new();
    private readonly List<SubscriberSink> sinks = new();
    private readonly IAssistantTurnIdempotencyRepository repository;
    private readonly AssistantTurnSignalRegistry signalRegistry;
    private readonly AssistantTurnSubscriptionRegistry subscriptionRegistry;
    private readonly TimeProvider timeProvider;
    private bool flushed;

    public BufferedAssistantTurnRecorder(
        Guid recordId,
        IAssistantTurnIdempotencyRepository repository,
        AssistantTurnSignalRegistry signalRegistry,
        AssistantTurnSubscriptionRegistry subscriptionRegistry,
        TimeProvider timeProvider)
    {
        RecordId = recordId;
        this.repository = repository;
        this.signalRegistry = signalRegistry;
        this.subscriptionRegistry = subscriptionRegistry;
        this.timeProvider = timeProvider;

        // Register before returning so a retry that races the producer's first Record call
        // still finds a publisher and attaches with an empty snapshot.
        subscriptionRegistry.Register(recordId, this);
    }

    public Guid RecordId { get; }

    public void Record(string eventName, string payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);

        var frame = new RecordedFrame(eventName, payload ?? "{}");
        SubscriberSink[] toPublish;
        lock (lockObj)
        {
            if (flushed)
            {
                // Recording after Flush is a programmer error; surface loudly in tests.
                throw new InvalidOperationException("Recorder has already been flushed.");
            }

            snapshot.Add(frame);
            toPublish = sinks.Count == 0 ? Array.Empty<SubscriberSink>() : sinks.ToArray();
        }

        // Publish outside the lock so a slow-subscriber `TryComplete` (which may run user
        // continuations) never holds back the producer thread or other subscribers.
        foreach (var sink in toPublish)
        {
            sink.TryPublishOrDrop(frame);
        }
    }

    public IAssistantTurnSubscription Subscribe(int capacity, TimeSpan lifetime)
    {
        lock (lockObj)
        {
            if (flushed)
            {
                // Producer already flushed; the snapshot is the entire event stream.
                return new SnapshotSubscription(snapshot.ToArray(), completed: true);
            }

            var sink = new SubscriberSink(capacity, lifetime, timeProvider);
            sinks.Add(sink);
            // Snapshot is captured atomically with sink registration so the subscriber sees
            // exactly the frames that were already produced + every frame produced after.
            return new LiveSubscription(snapshot.ToArray(), sink, RemoveSink);
        }
    }

    private void RemoveSink(SubscriberSink sink)
    {
        lock (lockObj)
        {
            sinks.Remove(sink);
        }
    }

    public async Task FlushAsync(AssistantTurnIdempotencyStatus terminalStatus, CancellationToken cancellationToken)
    {
        SubscriberSink[] toClose;
        string json;
        lock (lockObj)
        {
            if (flushed)
            {
                return;
            }

            flushed = true;
            json = JsonSerializer.Serialize(snapshot, JsonOptions);
            toClose = sinks.Count == 0 ? Array.Empty<SubscriberSink>() : sinks.ToArray();
            sinks.Clear();
        }

        try
        {
            await repository.MarkTerminalAsync(
                RecordId,
                terminalStatus,
                json,
                timeProvider.GetUtcNow().UtcDateTime,
                cancellationToken);
        }
        finally
        {
            // Always release subscribers + signal waiters even if persistence fails — a hung
            // subscriber is worse than a missing replay record.
            subscriptionRegistry.Unregister(RecordId);
            signalRegistry.Signal(RecordId);
            foreach (var sink in toClose)
            {
                sink.Complete();
            }
        }
    }
}

public sealed class AssistantTurnIdempotencyOptions
{
    /// <summary>
    /// How long an idempotency row remains valid before the background sweep removes it. Long
    /// enough for retries to land; short enough that it isn't a long-term store.
    /// </summary>
    public TimeSpan RecordTtl { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Polling cadence used while waiting for a same-conversation in-flight turn on a different
    /// instance to reach a terminal status. Same-instance completions wake the waiter via
    /// <see cref="AssistantTurnSignalRegistry"/> so this only governs the cross-instance path.
    /// </summary>
    public TimeSpan WaitPollInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Maximum time to wait for an in-flight duplicate to terminate before giving up.</summary>
    public TimeSpan WaitTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>How often the background sweep removes expired rows.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// sc-803 — Per-subscriber bounded channel capacity for live-tail reattach. A retry whose
    /// reader falls behind by more than this many frames is dropped (its channel is completed
    /// with an error) so the producer is never stalled.
    /// </summary>
    public int LiveTailSubscriberCapacity { get; set; } = 256;

    /// <summary>
    /// sc-803 — Maximum lifetime of a live-tail subscriber before its channel is closed with
    /// a timeout. Defaults to <see cref="WaitTimeout"/> when zero or negative — a producer
    /// that never flushes (process exit) shouldn't keep retries hanging forever.
    /// </summary>
    public TimeSpan LiveTailSubscriberLifetime { get; set; } = TimeSpan.Zero;
}
