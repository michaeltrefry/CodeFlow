using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;

namespace CodeFlow.Orchestration.Notifications.Providers.Email.Ses;

/// <summary>
/// Production <see cref="ISesEmailClient"/> backed by the real
/// <see cref="IAmazonSimpleEmailServiceV2"/> SDK client. Owns the underlying client (disposed
/// when the wrapping email provider is disposed by the registry's per-scope cache).
/// </summary>
public sealed class AwsSdkSesEmailClient : ISesEmailClient, IDisposable
{
    private readonly IAmazonSimpleEmailServiceV2 inner;

    public AwsSdkSesEmailClient(IAmazonSimpleEmailServiceV2 inner)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public Task<SendEmailResponse> SendEmailAsync(
        SendEmailRequest request,
        CancellationToken cancellationToken = default) =>
        inner.SendEmailAsync(request, cancellationToken);

    public void Dispose() => inner.Dispose();
}
