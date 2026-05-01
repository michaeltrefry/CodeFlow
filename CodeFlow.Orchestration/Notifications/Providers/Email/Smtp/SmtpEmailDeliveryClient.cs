using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace CodeFlow.Orchestration.Notifications.Providers.Email.Smtp;

/// <summary>
/// Generic SMTP relay via MailKit. Works against SES SMTP, SendGrid, on-prem postfix, etc.
/// One <see cref="SmtpClient"/> is created per send (cheap, isolates failures, avoids
/// long-lived connections that idle through firewalls). Auth is plain LOGIN over STARTTLS by
/// default; <see cref="SmtpEmailSettings.UseStartTls"/> can be turned off for unencrypted
/// dev relays.
/// </summary>
public sealed class SmtpEmailDeliveryClient : IEmailDeliveryClient
{
    private readonly SmtpEmailSettings settings;
    private readonly string? password;
    private readonly Func<ISmtpClient> smtpClientFactory;
    private readonly ILogger<SmtpEmailDeliveryClient> logger;

    public SmtpEmailDeliveryClient(
        SmtpEmailSettings settings,
        string? password,
        ILogger<SmtpEmailDeliveryClient> logger,
        Func<ISmtpClient>? smtpClientFactory = null)
    {
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.password = password;
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.smtpClientFactory = smtpClientFactory ?? (() => new SmtpClient());
    }

    public async Task<EmailDeliveryOutcome> SendAsync(
        EmailRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        MimeMessage mime;
        try
        {
            mime = new MimeMessage();
            mime.From.Add(MailboxAddress.Parse(request.FromAddress));
            mime.To.Add(MailboxAddress.Parse(request.ToAddress));
            mime.Subject = request.Subject ?? string.Empty;
            mime.Body = new TextPart("plain") { Text = request.TextBody };
        }
        catch (ParseException ex)
        {
            return EmailDeliveryOutcome.Failed("email.smtp.invalid_address", ex.Message);
        }

        using var client = smtpClientFactory();
        try
        {
            var secureSocketOptions = settings.UseStartTls
                ? SecureSocketOptions.StartTls
                : SecureSocketOptions.None;

            await client.ConnectAsync(settings.Host, settings.Port, secureSocketOptions, cancellationToken);

            if (!string.IsNullOrEmpty(settings.Username) && !string.IsNullOrEmpty(password))
            {
                await client.AuthenticateAsync(settings.Username, password, cancellationToken);
            }

            var providerMessageId = await client.SendAsync(mime, cancellationToken);
            await client.DisconnectAsync(quit: true, cancellationToken);

            // MailKit returns the server's response string from SendAsync (e.g. "250 OK queued
            // as ABC123"); use the MimeMessage Id which is stable across deliveries as our
            // ProviderMessageId, and put the server response in a debug log.
            logger.LogDebug(
                "SMTP server response for message {MessageId}: {ServerResponse}",
                mime.MessageId, providerMessageId);

            return EmailDeliveryOutcome.Sent(mime.MessageId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AuthenticationException ex)
        {
            return EmailDeliveryOutcome.Failed("email.smtp.auth_failed", ex.Message);
        }
        catch (SslHandshakeException ex)
        {
            return EmailDeliveryOutcome.Failed("email.smtp.tls_failed", ex.Message);
        }
        catch (SmtpCommandException ex)
        {
            return EmailDeliveryOutcome.Failed($"email.smtp.command_{(int)ex.StatusCode}", ex.Message);
        }
        catch (SmtpProtocolException ex)
        {
            return EmailDeliveryOutcome.Failed("email.smtp.protocol_error", ex.Message);
        }
        catch (TaskCanceledException ex)
        {
            return EmailDeliveryOutcome.Failed("email.smtp.timeout", ex.Message);
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException)
        {
            return EmailDeliveryOutcome.Failed("email.smtp.transport_error", ex.Message);
        }
    }
}
