using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;
using Microsoft.Extensions.Logging;

namespace CodeFlow.Orchestration.Notifications.Providers.Email.Ses;

/// <summary>
/// Amazon SES via the v2 SDK. The factory builds the underlying SDK client with explicit
/// credentials when the config row supplies them, otherwise relies on the SDK's default
/// credential chain (IAM role, env vars, shared profile, …) so production deployments can
/// pin to an instance role. Wrapped behind <see cref="ISesEmailClient"/> so tests can mock
/// just the one method we call.
/// </summary>
public sealed class SesEmailDeliveryClient : IEmailDeliveryClient
{
    private readonly ISesEmailClient sesClient;
    private readonly ILogger<SesEmailDeliveryClient> logger;

    public SesEmailDeliveryClient(
        ISesEmailClient sesClient,
        ILogger<SesEmailDeliveryClient> logger)
    {
        this.sesClient = sesClient ?? throw new ArgumentNullException(nameof(sesClient));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<EmailDeliveryOutcome> SendAsync(
        EmailRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sesRequest = new SendEmailRequest
        {
            FromEmailAddress = request.FromAddress,
            Destination = new Destination
            {
                ToAddresses = new List<string> { request.ToAddress },
            },
            Content = new EmailContent
            {
                Simple = new Message
                {
                    Subject = new Content
                    {
                        Charset = "UTF-8",
                        Data = request.Subject ?? string.Empty,
                    },
                    Body = new Body
                    {
                        Text = new Content
                        {
                            Charset = "UTF-8",
                            Data = request.TextBody,
                        },
                    },
                },
            },
        };

        try
        {
            var response = await sesClient.SendEmailAsync(sesRequest, cancellationToken);
            return EmailDeliveryOutcome.Sent(response.MessageId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AmazonSimpleEmailServiceV2Exception ex)
        {
            logger.LogWarning(ex,
                "SES SendEmail failed: {ErrorCode} {Message}", ex.ErrorCode, ex.Message);
            var errorCode = string.IsNullOrEmpty(ex.ErrorCode)
                ? "email.ses.unknown_error"
                : $"email.ses.{NormalizeErrorCode(ex.ErrorCode)}";
            return EmailDeliveryOutcome.Failed(errorCode, ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return EmailDeliveryOutcome.Failed("email.ses.transport_error", ex.Message);
        }
    }

    private static string NormalizeErrorCode(string awsErrorCode)
    {
        // SES error codes are typically PascalCase ("MessageRejected", "MailFromDomainNotVerified");
        // map to lower_snake_case for stable namespacing alongside slack.* / smtp.*.
        Span<char> buffer = stackalloc char[awsErrorCode.Length * 2];
        var written = 0;
        for (var i = 0; i < awsErrorCode.Length; i++)
        {
            var c = awsErrorCode[i];
            if (i > 0 && char.IsUpper(c))
            {
                buffer[written++] = '_';
            }
            buffer[written++] = char.ToLowerInvariant(c);
        }
        return new string(buffer[..written]);
    }
}
