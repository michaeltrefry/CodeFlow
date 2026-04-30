namespace CodeFlow.Orchestration.Notifications;

/// <summary>
/// Builds the canonical action URL embedded on every <c>HitlTaskPendingEvent</c>. Reviewers
/// click this link to land on the specific HITL handling surface. sc-53 ships a minimal
/// implementation; sc-62 owns the canonical route format and may refine the surface this
/// builder produces (e.g. dedicated <c>/hitl/{taskId}</c> route, fragment-based deep link, …).
/// </summary>
public interface IHitlNotificationActionUrlBuilder
{
    Uri BuildForPendingTask(long hitlTaskId, Guid traceId);
}
