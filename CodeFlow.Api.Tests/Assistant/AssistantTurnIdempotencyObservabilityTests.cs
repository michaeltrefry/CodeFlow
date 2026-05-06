using System.Collections.Concurrent;
using CodeFlow.Api.Assistant.Idempotency;
using CodeFlow.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeFlow.Api.Tests.Assistant;

/// <summary>
/// sc-807 — Asserts the dispatch outcome log + subscriber attach log carry the
/// recordId / conversationId properties ops keys off when chasing a "where did this
/// retry land?" report.
/// </summary>
public sealed class AssistantTurnIdempotencyObservabilityTests
{
    [Fact]
    public async Task DispatchAsync_emits_a_structured_outcome_log_with_record_and_conversation_ids()
    {
        var fixture = new Fixture();
        var conversationId = Guid.NewGuid();

        var outcome = await fixture.Coordinator.DispatchAsync(
            conversationId, "key-1", "user-a", "hash-1", CancellationToken.None);

        outcome.Should().BeOfType<AssistantTurnDispatchOutcome.Claimed>();
        var claimed = (AssistantTurnDispatchOutcome.Claimed)outcome;

        var dispatchEntry = fixture.CoordinatorSink.Records.SingleOrDefault(r =>
            r.Properties.ContainsKey("Outcome"));
        dispatchEntry.Should().NotBeNull("DispatchAsync must emit an Information-level outcome log");
        dispatchEntry!.Properties["Outcome"].Should().Be("Claimed");
        dispatchEntry.Properties["RecordId"].Should().Be(claimed.Record.Id);
        dispatchEntry.Properties["ConversationId"].Should().Be(conversationId);
    }

    [Fact]
    public async Task TrySubscribe_emits_an_attach_log_with_record_id_and_snapshot_count()
    {
        var fixture = new Fixture();
        var conversationId = Guid.NewGuid();
        var claimed = (AssistantTurnDispatchOutcome.Claimed)await fixture.Coordinator.DispatchAsync(
            conversationId, "key-1", "user-a", "hash-1", CancellationToken.None);

        // Produce a couple of frames so the late-attach snapshot is non-empty — the attach
        // log's `SnapshotFrames` is the actionable bit ops use to tell "joined late" from
        // "joined before any frames."
        claimed.Recorder.Record("text-delta", """{"delta":"a"}""");
        claimed.Recorder.Record("text-delta", """{"delta":"b"}""");

        var subscription = fixture.SubscriptionRegistry.TrySubscribe(claimed.Record.Id);
        subscription.Should().NotBeNull();

        var attachEntry = fixture.RegistrySink.Records.SingleOrDefault(r =>
            r.Properties.ContainsKey("SnapshotFrames"));
        attachEntry.Should().NotBeNull("TrySubscribe must emit an Information-level attach log");
        attachEntry!.Properties["RecordId"].Should().Be(claimed.Record.Id);
        attachEntry.Properties["SnapshotFrames"].Should().Be(2);
        attachEntry.Properties["CompletedAtAttach"].Should().Be(false);

        await subscription!.DisposeAsync();
    }

    private sealed class Fixture
    {
        public Fixture()
        {
            Repository = new InMemoryRepo();
            SignalRegistry = new AssistantTurnSignalRegistry();
            Options = Microsoft.Extensions.Options.Options.Create(new AssistantTurnIdempotencyOptions
            {
                LiveTailSubscriberCapacity = 64,
                LiveTailSubscriberLifetime = TimeSpan.FromSeconds(5),
            });
            CoordinatorSink = new RecordingLoggerSink();
            RegistrySink = new RecordingLoggerSink();
            SubscriptionRegistry = new AssistantTurnSubscriptionRegistry(
                Options,
                BuildLogger<AssistantTurnSubscriptionRegistry>(RegistrySink));
            Coordinator = new AssistantTurnIdempotencyCoordinator(
                Repository,
                SignalRegistry,
                SubscriptionRegistry,
                Options,
                BuildLogger<AssistantTurnIdempotencyCoordinator>(CoordinatorSink),
                TimeProvider.System);
        }

        public InMemoryRepo Repository { get; }
        public AssistantTurnSignalRegistry SignalRegistry { get; }
        public IOptions<AssistantTurnIdempotencyOptions> Options { get; }
        public AssistantTurnSubscriptionRegistry SubscriptionRegistry { get; }
        public AssistantTurnIdempotencyCoordinator Coordinator { get; }
        public RecordingLoggerSink CoordinatorSink { get; }
        public RecordingLoggerSink RegistrySink { get; }

        private static ILogger<T> BuildLogger<T>(RecordingLoggerSink sink) =>
            LoggerFactory.Create(b =>
            {
                b.AddProvider(new RecordingLoggerProvider(sink));
                b.SetMinimumLevel(LogLevel.Trace);
            }).CreateLogger<T>();
    }

    private sealed class InMemoryRepo : IAssistantTurnIdempotencyRepository
    {
        private readonly ConcurrentDictionary<(Guid, string), AssistantTurnIdempotencyRecord> rows = new();
        private readonly ConcurrentDictionary<Guid, AssistantTurnIdempotencyRecord> byId = new();
        private readonly object lockObj = new();

        public Task<AssistantTurnClaimOutcome> TryClaimAsync(
            Guid conversationId,
            string idempotencyKey,
            string userId,
            string requestHash,
            DateTime nowUtc,
            TimeSpan ttl,
            CancellationToken cancellationToken = default)
        {
            lock (lockObj)
            {
                var key = (conversationId, idempotencyKey);
                if (rows.TryGetValue(key, out var existing))
                {
                    return Task.FromResult<AssistantTurnClaimOutcome>(
                        new AssistantTurnClaimOutcome.Existing(existing));
                }

                var record = new AssistantTurnIdempotencyRecord(
                    Id: Guid.NewGuid(),
                    ConversationId: conversationId,
                    IdempotencyKey: idempotencyKey,
                    UserId: userId,
                    RequestHash: requestHash,
                    Status: AssistantTurnIdempotencyStatus.InFlight,
                    EventsJson: "[]",
                    CreatedAtUtc: nowUtc,
                    CompletedAtUtc: null,
                    ExpiresAtUtc: nowUtc + ttl);
                rows[key] = record;
                byId[record.Id] = record;
                return Task.FromResult<AssistantTurnClaimOutcome>(
                    new AssistantTurnClaimOutcome.Claimed(record));
            }
        }

        public Task<AssistantTurnIdempotencyRecord?> GetAsync(
            Guid conversationId, string idempotencyKey, CancellationToken cancellationToken = default)
        {
            rows.TryGetValue((conversationId, idempotencyKey), out var record);
            return Task.FromResult(record);
        }

        public Task<AssistantTurnIdempotencyRecord?> GetByIdAsync(
            Guid id, CancellationToken cancellationToken = default)
        {
            byId.TryGetValue(id, out var record);
            return Task.FromResult(record);
        }

        public Task MarkTerminalAsync(
            Guid id,
            AssistantTurnIdempotencyStatus terminalStatus,
            string eventsJson,
            DateTime completedAtUtc,
            CancellationToken cancellationToken = default)
        {
            lock (lockObj)
            {
                if (!byId.TryGetValue(id, out var record))
                {
                    throw new InvalidOperationException($"Record {id} not found.");
                }
                var updated = record with
                {
                    Status = terminalStatus,
                    EventsJson = eventsJson,
                    CompletedAtUtc = completedAtUtc,
                };
                byId[id] = updated;
                rows[(record.ConversationId, record.IdempotencyKey)] = updated;
            }
            return Task.CompletedTask;
        }

        public Task<int> PurgeExpiredAsync(DateTime nowUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }

    private sealed record RecordingEntry(
        LogLevel Level,
        string Message,
        IReadOnlyDictionary<string, object?> Properties);

    private sealed class RecordingLoggerSink
    {
        public List<RecordingEntry> Records { get; } = new();
    }

    private sealed class RecordingLoggerProvider(RecordingLoggerSink sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new RecordingLogger(sink);
        public void Dispose() { }
    }

    private sealed class RecordingLogger(RecordingLoggerSink sink) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (state is IReadOnlyList<KeyValuePair<string, object?>> structured)
            {
                foreach (var kvp in structured)
                {
                    properties[kvp.Key] = kvp.Value;
                }
            }
            sink.Records.Add(new RecordingEntry(logLevel, formatter(state, exception), properties));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
