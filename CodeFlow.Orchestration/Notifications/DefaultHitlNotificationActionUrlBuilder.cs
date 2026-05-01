using Microsoft.Extensions.Options;

namespace CodeFlow.Orchestration.Notifications;

/// <summary>
/// Default action URL builder. Produces the canonical HITL deep-link
/// <c>{baseUrl}/hitl?task={taskId}&amp;trace={traceId:D}</c>.
/// </summary>
/// <remarks>
/// The shape was pinned in sc-62 after the epic-48 open question. Trade-offs we accepted:
/// - Lands the reviewer in the queue so they see other pending items in one sweep, instead of
///   a single-task surface they have to back out of.
/// - <c>task</c> drives queue-row selection on first load (`HitlQueueComponent`).
/// - <c>trace</c> is a graceful fallback: if the task is no longer Pending by the time the
///   reviewer clicks (decided/cancelled in the meantime) the queue shows a chip linking to
///   <c>/traces/{traceId}</c> so the URL remains useful.
/// - Format changes are a breaking deep-link contract — historical notifications already in
///   inboxes embed this exact shape. Keep this builder authoritative; do not let providers
///   build their own URLs.
/// </remarks>
public sealed class DefaultHitlNotificationActionUrlBuilder : IHitlNotificationActionUrlBuilder
{
    private readonly IOptions<NotificationOptions> options;

    public DefaultHitlNotificationActionUrlBuilder(IOptions<NotificationOptions> options)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public Uri BuildForPendingTask(long hitlTaskId, Guid traceId)
    {
        var baseUrl = options.Value.PublicBaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException(
                $"NotificationOptions.PublicBaseUrl is not configured. Set '{NotificationOptions.SectionName}:PublicBaseUrl' " +
                "in configuration so HITL notifications can include a working action link.");
        }

        var trimmed = baseUrl.TrimEnd('/');
        return new Uri($"{trimmed}/hitl?task={hitlTaskId}&trace={traceId:D}", UriKind.Absolute);
    }
}
