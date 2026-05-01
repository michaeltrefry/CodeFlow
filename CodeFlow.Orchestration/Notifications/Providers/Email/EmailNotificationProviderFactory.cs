using Amazon;
using Amazon.Runtime;
using Amazon.SimpleEmailV2;
using CodeFlow.Contracts.Notifications;
using CodeFlow.Orchestration.Notifications.Providers.Email.Ses;
using CodeFlow.Orchestration.Notifications.Providers.Email.Smtp;
using CodeFlow.Persistence.Notifications;
using Microsoft.Extensions.Logging;

namespace CodeFlow.Orchestration.Notifications.Providers.Email;

/// <summary>
/// Single factory for the <see cref="NotificationChannel.Email"/> channel. Parses the engine
/// selector + engine-specific settings out of <c>NotificationProviderConfig.AdditionalConfigJson</c>,
/// builds the right <see cref="IEmailDeliveryClient"/>, and wraps it in an
/// <see cref="EmailNotificationProvider"/>. The single-factory-per-channel constraint enforced
/// by the registry (sc-54) means every email provider — SES, SMTP, future engines — flows
/// through this dispatch.
/// </summary>
public sealed class EmailNotificationProviderFactory : INotificationProviderFactory
{
    private readonly ILoggerFactory loggerFactory;
    private readonly TimeProvider clock;

    public EmailNotificationProviderFactory(
        ILoggerFactory loggerFactory,
        TimeProvider? clock = null)
    {
        this.loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        this.clock = clock ?? TimeProvider.System;
    }

    public NotificationChannel Channel => NotificationChannel.Email;

    public Task<INotificationProvider> CreateAsync(
        NotificationProviderConfigWithCredential config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        var settings = EmailProviderSettings.Parse(config.Config.AdditionalConfigJson);
        var client = settings.Engine switch
        {
            EmailEngine.Ses => BuildSesClient(config, settings.Ses!),
            EmailEngine.Smtp => BuildSmtpClient(config, settings.Smtp!),
            _ => throw new EmailProviderSettingsException($"Unhandled email engine {settings.Engine}."),
        };

        var provider = new EmailNotificationProvider(
            config,
            client,
            loggerFactory.CreateLogger<EmailNotificationProvider>(),
            clock);

        return Task.FromResult<INotificationProvider>(provider);
    }

    private IEmailDeliveryClient BuildSesClient(
        NotificationProviderConfigWithCredential config,
        SesEmailSettings sesSettings)
    {
        var credentials = SesEmailCredentials.Parse(config.PlaintextCredential);
        var region = RegionEndpoint.GetBySystemName(sesSettings.Region);
        var sdkClient = credentials is null
            ? new AmazonSimpleEmailServiceV2Client(region) // default credential chain
            : new AmazonSimpleEmailServiceV2Client(
                new BasicAWSCredentials(credentials.AccessKey, credentials.SecretKey),
                region);

        return new SesEmailDeliveryClient(
            new AwsSdkSesEmailClient(sdkClient),
            loggerFactory.CreateLogger<SesEmailDeliveryClient>());
    }

    private IEmailDeliveryClient BuildSmtpClient(
        NotificationProviderConfigWithCredential config,
        SmtpEmailSettings smtpSettings)
    {
        return new SmtpEmailDeliveryClient(
            smtpSettings,
            password: config.PlaintextCredential,
            loggerFactory.CreateLogger<SmtpEmailDeliveryClient>());
    }
}
