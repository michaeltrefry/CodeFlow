namespace CodeFlow.Contracts.Notifications;

/// <summary>
/// Provider-neutral notification event raised when a HITL task transitions to Pending and a
/// human reviewer needs to act. Shape mirrors <c>HitlTaskEntity</c> so providers/templates can
/// surface trace/round/node/workflow/agent context without reaching back into persistence.
/// </summary>
/// <param name="EventId">Stable id for this notification event (typically a fresh GUID at emit time).</param>
/// <param name="OccurredAtUtc">UTC time the event was generated.</param>
/// <param name="ActionUrl">Canonical CodeFlow deep-link to the HITL handling surface for this task. Required.</param>
/// <param name="Severity">Severity hint; defaults to <see cref="NotificationSeverity.Normal"/>.</param>
/// <param name="HitlTaskId">Persisted <c>HitlTaskEntity.Id</c> the event refers to.</param>
/// <param name="TraceId">Owning saga trace id.</param>
/// <param name="RoundId">Round id at the time the HITL task was created.</param>
/// <param name="NodeId">Workflow node that produced the HITL task.</param>
/// <param name="WorkflowKey">Workflow definition key.</param>
/// <param name="WorkflowVersion">Workflow definition version pinned at dispatch.</param>
/// <param name="AgentKey">Agent definition key whose invocation was suspended for review.</param>
/// <param name="AgentVersion">Agent definition version pinned at dispatch.</param>
/// <param name="HitlTaskCreatedAtUtc">UTC time the HITL task entity was persisted.</param>
/// <param name="InputPreview">Bounded preview (≤ ~2 KiB) of the input that triggered the HITL task. Optional.</param>
/// <param name="InputRef">Artifact URI for the full input payload. Optional.</param>
/// <param name="SubflowPath">Slash-delimited path of subflow node ids when the HITL task originates inside a subflow. Null at top level.</param>
public sealed record HitlTaskPendingEvent(
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    Uri ActionUrl,
    NotificationSeverity Severity,
    long HitlTaskId,
    Guid TraceId,
    Guid RoundId,
    Guid NodeId,
    string WorkflowKey,
    int WorkflowVersion,
    string AgentKey,
    int AgentVersion,
    DateTimeOffset HitlTaskCreatedAtUtc,
    string? InputPreview = null,
    Uri? InputRef = null,
    string? SubflowPath = null) : INotificationEvent
{
    public NotificationEventKind Kind => NotificationEventKind.HitlTaskPending;
}
