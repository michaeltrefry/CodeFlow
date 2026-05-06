using System.Collections.Concurrent;
using CodeFlow.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeFlow.Api.Assistant.Idempotency;

/// <summary>
/// sc-808 (AR-6) — Per-process registry of background turn-producer Tasks. The originating
/// endpoint hands the producer factory to <see cref="Start"/>, which spawns a Task that
/// runs the chat service stream and records each frame through the AR-1 multicast recorder.
/// The Task is intentionally NOT bound to any one request's <c>RequestAborted</c> token —
/// a client disconnect detaches the subscriber but never tears down the producer, so a
/// retry within record TTL still gets the full event stream.
/// </summary>
public interface IAssistantTurnTaskRegistry
{
    /// <summary>
    /// Spawns the producer task. Returns immediately — the caller is the first subscriber
    /// and reads frames through the recorder's subscription registry. The task owns the
    /// recorder's lifecycle: it calls <see cref="IAssistantTurnRecorder.FlushAsync"/> with
    /// <see cref="AssistantTurnIdempotencyStatus.Completed"/> on natural producer
    /// completion and <see cref="AssistantTurnIdempotencyStatus.Failed"/> on producer fault.
    /// On fault, a single synthetic <c>error</c> frame is recorded BEFORE flush so retries
    /// see a uniform terminal SSE shape.
    /// <para>
    /// sc-808 (AR-6): the registry runs the producer inside a fresh DI scope so request-
    /// scoped services (chat service, repositories, DbContext) survive a client disconnect.
    /// The producer factory receives that scope's <see cref="IServiceProvider"/>; resolve
    /// chat service / repositories from it, do NOT capture the request's scoped instances.
    /// </para>
    /// </summary>
    Task Start(
        Guid recordId,
        Func<IServiceProvider, CancellationToken, IAsyncEnumerable<AssistantTurnEvent>> producerFactory,
        IAssistantTurnRecorder recorder);

    /// <summary>Signals cancellation to the registered task; no-op if absent.</summary>
    void Cancel(Guid recordId);

    /// <summary>True when a task is registered and still running.</summary>
    bool TryGet(Guid recordId);
}

public sealed class AssistantTurnTaskRegistry : IAssistantTurnTaskRegistry
{
    private readonly ConcurrentDictionary<Guid, RunningTurn> tasks = new();
    private readonly IServiceScopeFactory scopeFactory;
    private readonly IOptions<AssistantTurnIdempotencyOptions> options;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<AssistantTurnTaskRegistry> logger;

    public AssistantTurnTaskRegistry(
        IServiceScopeFactory scopeFactory,
        IOptions<AssistantTurnIdempotencyOptions> options,
        TimeProvider timeProvider,
        ILogger<AssistantTurnTaskRegistry> logger)
    {
        this.scopeFactory = scopeFactory;
        this.options = options;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public Task Start(
        Guid recordId,
        Func<IServiceProvider, CancellationToken, IAsyncEnumerable<AssistantTurnEvent>> producerFactory,
        IAssistantTurnRecorder recorder)
    {
        ArgumentNullException.ThrowIfNull(producerFactory);
        ArgumentNullException.ThrowIfNull(recorder);

        // sc-809 (AR-7): hard ceiling on the turn task's lifetime. Default falls back to
        // RecordTtl so the task can never outlive the idempotency row that the sweep will
        // delete. When the ceiling fires, the producer sees a cancel and the recorder
        // flushes terminal Failed.
        var opts = options.Value;
        var maxLifetime = opts.MaxTurnLifetime > TimeSpan.Zero
            ? opts.MaxTurnLifetime
            : opts.RecordTtl;
        var cts = new CancellationTokenSource(maxLifetime, timeProvider);
        var task = Task.Run(() => RunProducerAsync(recordId, producerFactory, recorder, cts.Token));

        var running = new RunningTurn(task, cts);
        if (!tasks.TryAdd(recordId, running))
        {
            // Defensive: caller shouldn't Start twice for the same record. Cancel the spare
            // CTS we just made so it doesn't leak; the existing entry keeps running.
            cts.Cancel();
            cts.Dispose();
            throw new InvalidOperationException(
                $"A turn task is already registered for record {recordId}.");
        }

        // Detach: caller doesn't await the task. The task's finally clause removes the entry.
        _ = task.ContinueWith(
            _ => Cleanup(recordId),
            TaskScheduler.Default);

        logger.LogInformation(
            "Assistant turn task started recordId={RecordId}",
            recordId);
        return task;
    }

    public void Cancel(Guid recordId)
    {
        if (tasks.TryGetValue(recordId, out var running))
        {
            logger.LogInformation(
                "Assistant turn task cancel requested recordId={RecordId}",
                recordId);
            running.Cts.Cancel();
        }
    }

    public bool TryGet(Guid recordId) => tasks.ContainsKey(recordId);

    private async Task RunProducerAsync(
        Guid recordId,
        Func<IServiceProvider, CancellationToken, IAsyncEnumerable<AssistantTurnEvent>> producerFactory,
        IAssistantTurnRecorder recorder,
        CancellationToken taskToken)
    {
        var terminalStatus = AssistantTurnIdempotencyStatus.Completed;
        Exception? unhandled = null;
        // sc-811 (AR-8): the chat service catches producer exceptions and converts them to
        // in-band TurnFailed events (mapped to `error` frames). Track whether the producer
        // emitted one so we can classify the run as Failed even though its iterator exited
        // normally — "fault still ends in Failed and surfaces the error frame" per the card.
        var sawErrorFrame = false;
        // sc-808 (AR-6): every dependency the producer touches (chat service, repositories,
        // DbContext) is resolved from a fresh scope owned by the task. Disposing this scope
        // when the producer ends releases the DbContext + outbox bus + everything else
        // independent of the originating HTTP request's lifetime.
        await using var scope = scopeFactory.CreateAsyncScope();
        try
        {
            await foreach (var evt in producerFactory(scope.ServiceProvider, taskToken).WithCancellation(taskToken))
            {
                var (eventName, payload) = AssistantTurnFrameMapper.Map(evt);
                recorder.Record(eventName, payload);
                if (eventName == "error")
                {
                    sawErrorFrame = true;
                }
            }
            if (sawErrorFrame)
            {
                terminalStatus = AssistantTurnIdempotencyStatus.Failed;
            }
        }
        catch (OperationCanceledException) when (taskToken.IsCancellationRequested)
        {
            // Lifetime ceiling fired (AR-7) or someone called Cancel — terminal Failed.
            terminalStatus = AssistantTurnIdempotencyStatus.Failed;
        }
        catch (Exception ex)
        {
            terminalStatus = AssistantTurnIdempotencyStatus.Failed;
            unhandled = ex;
        }

        if (unhandled is not null)
        {
            // sc-808 (AR-6) — surface the producer fault as a synthetic in-band error frame so
            // the in-flight subscriber + any future replay see a uniform terminal SSE shape.
            try
            {
                var (eventName, payload) = AssistantTurnFrameMapper.BuildProducerFaultFrame(
                    "The assistant turn ended unexpectedly.");
                recorder.Record(eventName, payload);
            }
            catch (Exception recordEx)
            {
                logger.LogWarning(
                    recordEx,
                    "Failed to record terminal error frame for recordId={RecordId}",
                    recordId);
            }
            logger.LogWarning(
                unhandled,
                "Assistant turn producer faulted recordId={RecordId}",
                recordId);
        }

        try
        {
            // CancellationToken.None: we never want to abandon a recorded stream because the
            // task token tripped — the persistence write is the whole point of the run.
            await recorder.FlushAsync(terminalStatus, CancellationToken.None);
        }
        catch (Exception flushEx)
        {
            logger.LogError(
                flushEx,
                "Failed to flush idempotency record {RecordId} from background turn task.",
                recordId);
        }

        logger.LogInformation(
            "Assistant turn task completed recordId={RecordId} terminalStatus={TerminalStatus}",
            recordId,
            terminalStatus);
    }

    private void Cleanup(Guid recordId)
    {
        if (tasks.TryRemove(recordId, out var running))
        {
            running.Cts.Dispose();
        }
    }

    private sealed record RunningTurn(Task Task, CancellationTokenSource Cts);
}
