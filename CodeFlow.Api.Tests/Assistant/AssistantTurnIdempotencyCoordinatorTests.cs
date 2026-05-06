using System.Collections.Concurrent;
using CodeFlow.Api.Assistant.Idempotency;
using CodeFlow.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CodeFlow.Api.Tests.Assistant;

/// <summary>
/// sc-804 — Coordinator dispatch tests. Covers the full <c>AssistantTurnDispatchOutcome</c>
/// switch (Claimed / HashMismatch / UserMismatch / Replay / WaitThenReplay / LiveTail) plus
/// the AR-2 race where the producer flushes between the status check and the registry query.
/// </summary>
public sealed class AssistantTurnIdempotencyCoordinatorTests
{
    private const string UserA = "user-a";
    private const string UserB = "user-b";
    private const string HashOne = "hash-1";
    private const string HashTwo = "hash-2";

    [Fact]
    public async Task First_request_is_claimed_with_an_active_recorder()
    {
        var harness = new Harness();
        var conversationId = Guid.NewGuid();
        var key = "key-1";

        var outcome = await harness.Coordinator.DispatchAsync(
            conversationId, key, UserA, HashOne, CancellationToken.None);

        var claimed = outcome.Should().BeOfType<AssistantTurnDispatchOutcome.Claimed>().Subject;
        claimed.Record.UserId.Should().Be(UserA);
        claimed.Record.Status.Should().Be(AssistantTurnIdempotencyStatus.InFlight);
        claimed.Recorder.Should().NotBeNull();

        // The recorder registers itself with the subscription registry — a same-instance
        // retry can attach immediately, before the recorder records its first frame.
        harness.Subscriptions.TrySubscribe(claimed.Record.Id).Should().NotBeNull();
    }

    [Fact]
    public async Task Same_user_same_hash_inflight_with_local_producer_returns_LiveTail()
    {
        var harness = new Harness();
        var conversationId = Guid.NewGuid();
        var key = "key-1";

        // Originating turn — claims and registers a recorder.
        var first = (AssistantTurnDispatchOutcome.Claimed)await harness.Coordinator.DispatchAsync(
            conversationId, key, UserA, HashOne, CancellationToken.None);
        first.Recorder.Record("text-delta", """{"delta":"hi"}""");

        // Retry from the same user with the same body while the original is still in flight.
        var retry = await harness.Coordinator.DispatchAsync(
            conversationId, key, UserA, HashOne, CancellationToken.None);

        var live = retry.Should().BeOfType<AssistantTurnDispatchOutcome.LiveTail>().Subject;
        live.Record.Id.Should().Be(first.Record.Id);
        live.Subscription.Should().NotBeNull();
        live.Subscription.Snapshot.Select(f => f.Event).Should().Equal("text-delta");

        await live.Subscription.DisposeAsync();
    }

    [Fact]
    public async Task Inflight_record_with_no_local_producer_falls_back_to_WaitThenReplay()
    {
        var harness = new Harness();
        var conversationId = Guid.NewGuid();
        var key = "key-1";

        // Plant an in-flight row directly in the repo without registering a recorder, to
        // simulate a cross-instance originating turn (the local registry has no entry).
        var record = harness.Repository.PlantInFlight(conversationId, key, UserA, HashOne);

        var retry = await harness.Coordinator.DispatchAsync(
            conversationId, key, UserA, HashOne, CancellationToken.None);

        var wait = retry.Should().BeOfType<AssistantTurnDispatchOutcome.WaitThenReplay>().Subject;
        wait.Record.Id.Should().Be(record.Id);
    }

    [Fact]
    public async Task Terminal_record_returns_Replay()
    {
        var harness = new Harness();
        var conversationId = Guid.NewGuid();
        var key = "key-1";
        harness.Repository.PlantTerminal(
            conversationId, key, UserA, HashOne,
            AssistantTurnIdempotencyStatus.Completed,
            eventsJson: """[{"event":"text-delta","payload":"{\"delta\":\"hi\"}"}]""");

        var outcome = await harness.Coordinator.DispatchAsync(
            conversationId, key, UserA, HashOne, CancellationToken.None);

        var replay = outcome.Should().BeOfType<AssistantTurnDispatchOutcome.Replay>().Subject;
        replay.Record.Status.Should().Be(AssistantTurnIdempotencyStatus.Completed);
    }

    [Fact]
    public async Task Different_user_returns_UserMismatch_to_avoid_leaking_existence()
    {
        var harness = new Harness();
        var conversationId = Guid.NewGuid();
        var key = "key-1";
        harness.Repository.PlantInFlight(conversationId, key, UserA, HashOne);

        var outcome = await harness.Coordinator.DispatchAsync(
            conversationId, key, UserB, HashOne, CancellationToken.None);

        outcome.Should().BeOfType<AssistantTurnDispatchOutcome.UserMismatch>();
    }

    [Fact]
    public async Task Same_user_different_hash_returns_HashMismatch()
    {
        var harness = new Harness();
        var conversationId = Guid.NewGuid();
        var key = "key-1";
        harness.Repository.PlantInFlight(conversationId, key, UserA, HashOne);

        var outcome = await harness.Coordinator.DispatchAsync(
            conversationId, key, UserA, HashTwo, CancellationToken.None);

        outcome.Should().BeOfType<AssistantTurnDispatchOutcome.HashMismatch>();
    }

    [Fact]
    public async Task Producer_flushing_during_dispatch_does_not_yield_a_closed_LiveTail()
    {
        // AR-2 race spec: producer flushes between the status read and the registry query.
        // The contract: dispatch must NOT return a LiveTail whose live channel is closed
        // and missing frames. Either Replay (record now terminal) or LiveTail whose
        // snapshot covers everything is acceptable.
        var harness = new Harness();
        var conversationId = Guid.NewGuid();
        var key = "key-1";

        var first = (AssistantTurnDispatchOutcome.Claimed)await harness.Coordinator.DispatchAsync(
            conversationId, key, UserA, HashOne, CancellationToken.None);
        first.Recorder.Record("text-delta", """{"delta":"a"}""");
        first.Recorder.Record("text-delta", """{"delta":"b"}""");

        // The simplest deterministic version of the race: flush completes, then the retry
        // dispatches. The repo row is now Completed, so we should land on Replay (covers
        // the flushed-before-dispatch path). The "LiveTail with Completed=true" branch is
        // covered indirectly through AR-1's after-flush Subscribe test.
        await first.Recorder.FlushAsync(AssistantTurnIdempotencyStatus.Completed, CancellationToken.None);

        var retry = await harness.Coordinator.DispatchAsync(
            conversationId, key, UserA, HashOne, CancellationToken.None);

        var replay = retry.Should().BeOfType<AssistantTurnDispatchOutcome.Replay>().Subject;
        replay.Record.Status.Should().Be(AssistantTurnIdempotencyStatus.Completed);
    }

    [Fact]
    public async Task LiveTail_subscription_streams_subsequent_frames_until_producer_flushes()
    {
        var harness = new Harness();
        var conversationId = Guid.NewGuid();
        var key = "key-1";

        var first = (AssistantTurnDispatchOutcome.Claimed)await harness.Coordinator.DispatchAsync(
            conversationId, key, UserA, HashOne, CancellationToken.None);
        first.Recorder.Record("text-delta", """{"delta":"a"}""");

        var retry = (AssistantTurnDispatchOutcome.LiveTail)await harness.Coordinator.DispatchAsync(
            conversationId, key, UserA, HashOne, CancellationToken.None);

        var liveFrames = new List<RecordedFrame>();
        var collect = Task.Run(async () =>
        {
            await foreach (var f in retry.Subscription.ReadAllAsync(CancellationToken.None))
            {
                liveFrames.Add(f);
            }
        });

        first.Recorder.Record("text-delta", """{"delta":"b"}""");
        first.Recorder.Record("text-delta", """{"delta":"c"}""");
        await first.Recorder.FlushAsync(AssistantTurnIdempotencyStatus.Completed, CancellationToken.None);
        await collect.WaitAsync(TimeSpan.FromSeconds(2));

        retry.Subscription.Snapshot.Select(f => f.Payload).Should().Equal("""{"delta":"a"}""");
        liveFrames.Select(f => f.Payload).Should()
            .Equal("""{"delta":"b"}""", """{"delta":"c"}""");

        await retry.Subscription.DisposeAsync();
    }

    private sealed class Harness
    {
        public Harness()
        {
            Repository = new InMemoryRepo();
            SignalRegistry = new AssistantTurnSignalRegistry();
            Options = Microsoft.Extensions.Options.Options.Create(new AssistantTurnIdempotencyOptions
            {
                LiveTailSubscriberCapacity = 64,
                LiveTailSubscriberLifetime = TimeSpan.FromSeconds(5),
            });
            Subscriptions = new AssistantTurnSubscriptionRegistry(
                Options,
                NullLogger<AssistantTurnSubscriptionRegistry>.Instance);
            Coordinator = new AssistantTurnIdempotencyCoordinator(
                Repository,
                SignalRegistry,
                Subscriptions,
                Options,
                NullLogger<AssistantTurnIdempotencyCoordinator>.Instance,
                TimeProvider.System);
        }

        public InMemoryRepo Repository { get; }
        public AssistantTurnSignalRegistry SignalRegistry { get; }
        public AssistantTurnSubscriptionRegistry Subscriptions { get; }
        public IOptions<AssistantTurnIdempotencyOptions> Options { get; }
        public AssistantTurnIdempotencyCoordinator Coordinator { get; }
    }

    /// <summary>
    /// Minimal in-memory <see cref="IAssistantTurnIdempotencyRepository"/> with the
    /// dispatch-relevant semantics: <c>TryClaimAsync</c> inserts an InFlight row keyed on
    /// (conversationId, idempotencyKey) and surfaces an existing row otherwise;
    /// <c>MarkTerminalAsync</c> finalises the row.
    /// </summary>
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
            Guid conversationId,
            string idempotencyKey,
            CancellationToken cancellationToken = default)
        {
            rows.TryGetValue((conversationId, idempotencyKey), out var record);
            return Task.FromResult(record);
        }

        public Task<AssistantTurnIdempotencyRecord?> GetByIdAsync(
            Guid id,
            CancellationToken cancellationToken = default)
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

        // Test helpers — let test cases plant rows in arbitrary states without going through
        // the recorder/coordinator path.

        public AssistantTurnIdempotencyRecord PlantInFlight(
            Guid conversationId,
            string idempotencyKey,
            string userId,
            string requestHash)
        {
            var record = new AssistantTurnIdempotencyRecord(
                Id: Guid.NewGuid(),
                ConversationId: conversationId,
                IdempotencyKey: idempotencyKey,
                UserId: userId,
                RequestHash: requestHash,
                Status: AssistantTurnIdempotencyStatus.InFlight,
                EventsJson: "[]",
                CreatedAtUtc: DateTime.UtcNow,
                CompletedAtUtc: null,
                ExpiresAtUtc: DateTime.UtcNow.AddMinutes(10));
            rows[(conversationId, idempotencyKey)] = record;
            byId[record.Id] = record;
            return record;
        }

        public AssistantTurnIdempotencyRecord PlantTerminal(
            Guid conversationId,
            string idempotencyKey,
            string userId,
            string requestHash,
            AssistantTurnIdempotencyStatus status,
            string eventsJson)
        {
            var now = DateTime.UtcNow;
            var record = new AssistantTurnIdempotencyRecord(
                Id: Guid.NewGuid(),
                ConversationId: conversationId,
                IdempotencyKey: idempotencyKey,
                UserId: userId,
                RequestHash: requestHash,
                Status: status,
                EventsJson: eventsJson,
                CreatedAtUtc: now,
                CompletedAtUtc: now,
                ExpiresAtUtc: now.AddMinutes(10));
            rows[(conversationId, idempotencyKey)] = record;
            byId[record.Id] = record;
            return record;
        }
    }
}
