namespace CodeFlow.Orchestration.Notifications.Providers.Email;

/// <summary>
/// Engine-neutral send surface used by <see cref="EmailNotificationProvider"/>. SES and SMTP
/// implementations live alongside in <see cref="EmailEngine"/>-specific subdirectories. The
/// outcome is provider-shaped (no Slack/Twilio leakage) so the email provider can build a
/// <c>NotificationDeliveryResult</c> without knowing which engine ran.
/// </summary>
public interface IEmailDeliveryClient
{
    Task<EmailDeliveryOutcome> SendAsync(EmailRequest request, CancellationToken cancellationToken = default);
}

public sealed record EmailRequest(
    string FromAddress,
    string ToAddress,
    string? Subject,
    string TextBody);

public sealed record EmailDeliveryOutcome(
    bool Success,
    string? ProviderMessageId,
    string? ErrorCode,
    string? ErrorMessage)
{
    public static EmailDeliveryOutcome Sent(string? providerMessageId) =>
        new(Success: true, ProviderMessageId: providerMessageId, ErrorCode: null, ErrorMessage: null);

    public static EmailDeliveryOutcome Failed(string errorCode, string? errorMessage) =>
        new(Success: false, ProviderMessageId: null, ErrorCode: errorCode, ErrorMessage: errorMessage);
}
