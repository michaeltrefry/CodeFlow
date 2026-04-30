namespace CodeFlow.Contracts.Notifications;

/// <summary>
/// Discriminator for provider-neutral notification events. New event types add a value here
/// and a corresponding <see cref="INotificationEvent"/> record. Routes/templates key off this
/// enum, never off the concrete record type.
/// </summary>
/// <remarks>
/// Slice sc-50 ships <see cref="HitlTaskPending"/> only. The reserved values are documented so
/// future slices (HITL decision/cancellation/reminder/timeout/escalation per epic 48) can
/// extend the contract without renumbering.
/// </remarks>
public enum NotificationEventKind
{
    Unspecified = 0,
    HitlTaskPending = 1,

    // Reserved for future HITL events. Do not reuse these numbers when adding a different
    // event family; assign 100+ for non-HITL kinds.
    // HitlTaskDecided = 2,
    // HitlTaskCancelled = 3,
    // HitlTaskReminder = 4,
    // HitlTaskTimeout = 5,
    // HitlTaskEscalated = 6,
}
