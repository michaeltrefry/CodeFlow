namespace CodeFlow.Contracts.Notifications;

/// <summary>
/// Transport family a notification provider speaks.
/// </summary>
/// <remarks>
/// Channels are deliberately coarse — concrete provider identity (e.g. "slack-prod-#hitl",
/// "smtp-mailgun") lives on <see cref="NotificationRoute.ProviderId"/>. Adding a new family
/// (Webhook, Teams, Discord, …) is a contract change here plus a provider implementation;
/// existing routes/templates remain untouched.
/// </remarks>
public enum NotificationChannel
{
    Unspecified = 0,
    Email = 1,
    Sms = 2,
    Slack = 3
}
