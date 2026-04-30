using Microsoft.Extensions.Options;

namespace CodeFlow.Orchestration.Notifications;

/// <summary>
/// Default action URL builder. Produces <c>{baseUrl}/hitl?task={taskId}&trace={traceId}</c>.
/// The existing <c>/hitl</c> queue route renders a full list today; sc-62 will add a
/// per-task landing surface that consumes the <c>task</c> query parameter.
/// </summary>
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
