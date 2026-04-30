using CodeFlow.Contracts.Notifications;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace CodeFlow.Orchestration.Notifications;

/// <summary>
/// MassTransit consumer that hands a <see cref="HitlTaskPendingEvent"/> off to the
/// notification dispatcher. The saga publishes the event after persisting the HITL task; this
/// consumer runs on a separate message so notification delivery failures can never roll back
/// the HITL row. The dispatcher (sc-52) catches all transport/template errors internally, so
/// this consumer's job is just the handoff.
/// </summary>
public sealed class HitlTaskPendingEventNotificationConsumer : IConsumer<HitlTaskPendingEvent>
{
    private readonly INotificationDispatcher dispatcher;
    private readonly ILogger<HitlTaskPendingEventNotificationConsumer> logger;

    public HitlTaskPendingEventNotificationConsumer(
        INotificationDispatcher dispatcher,
        ILogger<HitlTaskPendingEventNotificationConsumer> logger)
    {
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task Consume(ConsumeContext<HitlTaskPendingEvent> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var evt = context.Message;
        logger.LogInformation(
            "Dispatching HitlTaskPending notification — event {EventId}, task {TaskId}, trace {TraceId}.",
            evt.EventId, evt.HitlTaskId, evt.TraceId);

        var results = await dispatcher.DispatchAsync(evt, context.CancellationToken);

        logger.LogInformation(
            "HitlTaskPending dispatch completed for event {EventId}: {AttemptCount} attempt(s) recorded.",
            evt.EventId, results.Count);
    }
}
