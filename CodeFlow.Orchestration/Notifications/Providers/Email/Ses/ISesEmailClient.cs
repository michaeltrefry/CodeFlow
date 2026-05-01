using Amazon.SimpleEmailV2.Model;

namespace CodeFlow.Orchestration.Notifications.Providers.Email.Ses;

/// <summary>
/// Narrow wrapper around the SES v2 SDK so <see cref="SesEmailDeliveryClient"/> can be unit
/// tested without faking the full <see cref="Amazon.SimpleEmailV2.IAmazonSimpleEmailServiceV2"/>
/// surface area (50+ methods). Production wires this to <see cref="AwsSdkSesEmailClient"/>.
/// </summary>
public interface ISesEmailClient
{
    Task<SendEmailResponse> SendEmailAsync(SendEmailRequest request, CancellationToken cancellationToken = default);
}
