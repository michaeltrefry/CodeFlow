namespace CodeFlow.Contracts.Notifications;

/// <summary>
/// Severity hint travelling with a notification event. Routes can filter on minimum severity
/// so noisy channels (e.g. SMS) only fire for higher-priority HITL events.
/// </summary>
public enum NotificationSeverity
{
    Info = 0,
    Normal = 1,
    High = 2,
    Urgent = 3
}
