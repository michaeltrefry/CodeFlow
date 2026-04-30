namespace CodeFlow.Orchestration.Notifications;

/// <summary>
/// Options for the notification subsystem. Bound from configuration section
/// <c>CodeFlow:Notifications</c>. Other notification config (provider settings, routes,
/// templates) lives in the database — this options class only carries process-wide settings
/// the dispatcher cannot infer at runtime.
/// </summary>
public sealed class NotificationOptions
{
    public const string SectionName = "CodeFlow:Notifications";

    /// <summary>
    /// Public base URL of the CodeFlow UI as reviewers reach it (e.g. <c>https://codeflow.example.com</c>).
    /// Used by <see cref="IHitlNotificationActionUrlBuilder"/> to produce the canonical
    /// notification action link. Trailing slash is tolerated and stripped at use time.
    /// </summary>
    public string? PublicBaseUrl { get; set; }
}
