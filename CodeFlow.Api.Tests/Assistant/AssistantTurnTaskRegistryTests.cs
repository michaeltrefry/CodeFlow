using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using CodeFlow.Api.Assistant;
using CodeFlow.Api.Assistant.Idempotency;
using CodeFlow.Persistence;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CodeFlow.Api.Tests.Assistant;

/// <summary>
/// sc-808 (AR-6) — Unit tests for the background turn-task registry. Verifies the producer
/// runs detached from the caller, the recorder receives mapped frames, FlushAsync lands at
/// terminal status (Completed on natural completion, Failed on producer fault), and Cancel
/// brings the producer down deterministically.
/// </summary>
public sealed class AssistantTurnTaskRegistryTests
{
    [Fact]
    public async Task Start_runs_producer_detached_and_records_each_frame_then_flushes_completed()
    {
        var fixture = NewFixture();
        var recorder = fixture.NewRecorder();

        var task = fixture.Registry.Start(
            recorder.RecordId,
            (sp, ct) => ProducerYielding(new[]
            {
                (AssistantTurnEvent)new TextDelta("a"),
                new TextDelta("b"),
            }, ct),
            recorder);

        await task.WaitAsync(TimeSpan.FromSeconds(2));

        // Recorder's persisted stream contains both frames + nothing else (no synthetic error
        // frame on the happy path). EventsJson is JsonSerializer-encoded so the inner-payload
        // quotes show up as `"`; we assert on the event-name field which doesn't get
        // double-escaped.
        var terminal = fixture.Repository.Terminals.Should().ContainSingle().Subject;
        terminal.Status.Should().Be(AssistantTurnIdempotencyStatus.Completed);
        var deltaCount = System.Text.RegularExpressions.Regex.Matches(
            terminal.EventsJson, "\"event\":\"text-delta\"").Count;
        deltaCount.Should().Be(2);
        terminal.EventsJson.Should().NotContain("\"event\":\"error\"");
        fixture.Registry.TryGet(recorder.RecordId).Should().BeFalse(
            "the registry must remove the entry after the task completes");
    }

    [Fact]
    public async Task Producer_fault_records_synthetic_error_frame_and_flushes_failed()
    {
        var fixture = NewFixture();
        var recorder = fixture.NewRecorder();

        var task = fixture.Registry.Start(
            recorder.RecordId,
            (sp, ct) => ProducerThatThrowsAfterFirst(ct),
            recorder);

        await task.WaitAsync(TimeSpan.FromSeconds(2));

        var terminal = fixture.Repository.Terminals.Should().ContainSingle().Subject;
        terminal.Status.Should().Be(AssistantTurnIdempotencyStatus.Failed);
        // The single TextDelta + the synthetic terminal error frame both land in the stream.
        terminal.EventsJson.Should().Contain("\"event\":\"text-delta\"");
        terminal.EventsJson.Should().Contain("\"event\":\"error\"");
        terminal.EventsJson.Should().Contain("ended unexpectedly");
    }

    [Fact]
    public async Task Cancel_brings_a_running_task_down_with_terminal_failed()
    {
        var fixture = NewFixture();
        var recorder = fixture.NewRecorder();
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var task = fixture.Registry.Start(
            recorder.RecordId,
            (sp, ct) => ProducerThatBlocksOn(release.Task, ct),
            recorder);

        // Give the producer a moment to actually enter the blocking await.
        await WaitUntilAsync(() => fixture.Registry.TryGet(recorder.RecordId), TimeSpan.FromSeconds(2));

        fixture.Registry.Cancel(recorder.RecordId);

        await task.WaitAsync(TimeSpan.FromSeconds(2));

        var terminal = fixture.Repository.Terminals.Should().ContainSingle().Subject;
        terminal.Status.Should().Be(AssistantTurnIdempotencyStatus.Failed,
            "cancellation via the registry's CTS terminates with Failed");

        // Release the gate so the producer's `using` cleanup completes; the task above is
        // already done so this is just hygiene for the producer-side TCS.
        release.SetResult(true);
    }

    [Fact]
    public async Task Subscriber_sees_every_recorded_frame_when_attached_before_Start()
    {
        var fixture = NewFixture();
        var recorder = fixture.NewRecorder();
        var subscription = fixture.SubscriptionRegistry.TrySubscribe(recorder.RecordId)!;
        subscription.Should().NotBeNull();

        var collected = new List<RecordedFrame>();
        var collector = Task.Run(async () =>
        {
            await foreach (var frame in subscription.ReadAllAsync(CancellationToken.None))
            {
                collected.Add(frame);
            }
        });

        var task = fixture.Registry.Start(
            recorder.RecordId,
            (sp, ct) => ProducerYielding(new[]
            {
                (AssistantTurnEvent)new TextDelta("alpha"),
                new TextDelta("beta"),
                new TextDelta("gamma"),
            }, ct),
            recorder);

        await task.WaitAsync(TimeSpan.FromSeconds(2));
        await collector.WaitAsync(TimeSpan.FromSeconds(2));

        collected.Select(f => f.Event).Should().Equal("text-delta", "text-delta", "text-delta");
        await subscription.DisposeAsync();
    }

    [Fact]
    public async Task Background_task_completes_even_when_no_subscriber_is_attached()
    {
        // Mirrors the AR-6 fire-and-forget case: originating client disconnects before any
        // subscriber attaches; the producer keeps running and flushes terminal anyway.
        var fixture = NewFixture();
        var recorder = fixture.NewRecorder();

        var task = fixture.Registry.Start(
            recorder.RecordId,
            (sp, ct) => ProducerYielding(new[]
            {
                (AssistantTurnEvent)new TextDelta("one"),
            }, ct),
            recorder);

        await task.WaitAsync(TimeSpan.FromSeconds(2));

        fixture.Repository.Terminals.Should().ContainSingle()
            .Which.Status.Should().Be(AssistantTurnIdempotencyStatus.Completed);
    }

    [Fact]
    public async Task Lifetime_ceiling_fires_and_terminates_a_blocked_producer_with_failed()
    {
        // sc-809 (AR-7): a wedged producer (e.g. stuck LLM call) must eventually be reaped
        // by the MaxTurnLifetime ceiling so the idempotency row reaches a terminal status
        // before its TTL sweep runs.
        var fixture = NewFixture(maxTurnLifetime: TimeSpan.FromMilliseconds(150));
        var recorder = fixture.NewRecorder();
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var task = fixture.Registry.Start(
            recorder.RecordId,
            (sp, ct) => ProducerThatBlocksOn(release.Task, ct),
            recorder);

        await task.WaitAsync(TimeSpan.FromSeconds(3));

        var terminal = fixture.Repository.Terminals.Should().ContainSingle().Subject;
        terminal.Status.Should().Be(AssistantTurnIdempotencyStatus.Failed,
            "lifetime ceiling cancellation lands as Failed, same path as Cancel()");

        // Hygiene: release the gate so the producer's awaiter unwinds (the task above is
        // already complete via cancellation, but the TCS shouldn't be left dangling).
        release.SetResult(true);
    }

    [Fact]
    public async Task Start_throws_if_a_task_is_already_registered_for_the_same_record()
    {
        var fixture = NewFixture();
        var recorder = fixture.NewRecorder();
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var task = fixture.Registry.Start(
            recorder.RecordId,
            (sp, ct) => ProducerThatBlocksOn(release.Task, ct),
            recorder);

        await WaitUntilAsync(() => fixture.Registry.TryGet(recorder.RecordId), TimeSpan.FromSeconds(2));

        Action act = () => fixture.Registry.Start(
            recorder.RecordId,
            (sp, ct) => ProducerYielding(Array.Empty<AssistantTurnEvent>(), ct),
            recorder);
        act.Should().Throw<InvalidOperationException>().WithMessage("*already registered*");

        // Release so the original task drains.
        release.SetResult(true);
        await task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static async IAsyncEnumerable<AssistantTurnEvent> ProducerYielding(
        IReadOnlyList<AssistantTurnEvent> items,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<AssistantTurnEvent> ProducerThatThrowsAfterFirst(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new TextDelta("a");
        await Task.Yield();
        throw new InvalidOperationException("boom");
    }

    private static async IAsyncEnumerable<AssistantTurnEvent> ProducerThatBlocksOn(
        Task gate,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        yield return new TextDelta("after-gate");
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!predicate() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
        predicate().Should().BeTrue("predicate should have flipped within the timeout");
    }

    private static Fixture NewFixture(TimeSpan? maxTurnLifetime = null) => new(maxTurnLifetime);

    private sealed class Fixture
    {
        public Fixture(TimeSpan? maxTurnLifetime = null)
        {
            Repository = new InMemoryAssistantTurnIdempotencyRepository();
            SignalRegistry = new AssistantTurnSignalRegistry();
            var options = Microsoft.Extensions.Options.Options.Create(new AssistantTurnIdempotencyOptions
            {
                LiveTailSubscriberCapacity = 64,
                LiveTailSubscriberLifetime = TimeSpan.FromSeconds(5),
                MaxTurnLifetime = maxTurnLifetime ?? TimeSpan.FromSeconds(30),
            });
            SubscriptionRegistry = new AssistantTurnSubscriptionRegistry(
                options,
                NullLogger<AssistantTurnSubscriptionRegistry>.Instance);
            // sc-808 (AR-6): the recorder + the registry both resolve their dependencies
            // through IServiceScopeFactory so the producer's scope outlives the request.
            var services = new ServiceCollection();
            services.AddSingleton<IAssistantTurnIdempotencyRepository>(Repository);
            ServiceProvider = services.BuildServiceProvider();
            ScopeFactory = ServiceProvider.GetRequiredService<IServiceScopeFactory>();
            Registry = new AssistantTurnTaskRegistry(
                ScopeFactory,
                options,
                TimeProvider.System,
                NullLogger<AssistantTurnTaskRegistry>.Instance);
        }

        public InMemoryAssistantTurnIdempotencyRepository Repository { get; }
        public AssistantTurnSignalRegistry SignalRegistry { get; }
        public AssistantTurnSubscriptionRegistry SubscriptionRegistry { get; }
        public AssistantTurnTaskRegistry Registry { get; }
        public ServiceProvider ServiceProvider { get; }
        public IServiceScopeFactory ScopeFactory { get; }

        public BufferedAssistantTurnRecorder NewRecorder() => new(
            Guid.NewGuid(),
            ScopeFactory,
            SignalRegistry,
            SubscriptionRegistry,
            TimeProvider.System,
            NullLogger<BufferedAssistantTurnRecorder>.Instance);
    }

    private sealed class InMemoryAssistantTurnIdempotencyRepository : IAssistantTurnIdempotencyRepository
    {
        public List<(Guid Id, AssistantTurnIdempotencyStatus Status, string EventsJson)> Terminals { get; } = new();

        public Task<AssistantTurnClaimOutcome> TryClaimAsync(
            Guid conversationId, string idempotencyKey, string userId, string requestHash,
            DateTime nowUtc, TimeSpan ttl, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<AssistantTurnIdempotencyRecord?> GetAsync(
            Guid conversationId, string idempotencyKey, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<AssistantTurnIdempotencyRecord?> GetByIdAsync(
            Guid id, CancellationToken cancellationToken = default) =>
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
}
