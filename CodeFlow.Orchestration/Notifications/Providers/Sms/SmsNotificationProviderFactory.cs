using CodeFlow.Contracts.Notifications;
using CodeFlow.Orchestration.Notifications.Providers.Sms.Twilio;
using CodeFlow.Persistence.Notifications;
using Microsoft.Extensions.Logging;

namespace CodeFlow.Orchestration.Notifications.Providers.Sms;

/// <summary>
/// SMS channel factory. v1 is hardcoded to Twilio; if a second SMS engine (Vonage, AWS SNS,
/// Plivo, …) ships later, this class will refactor to dispatch on an
/// <c>additional_config_json</c> engine selector exactly like
/// <see cref="Email.EmailNotificationProviderFactory"/> does for SES vs SMTP.
/// </summary>
public sealed class SmsNotificationProviderFactory : INotificationProviderFactory
{
    /// <summary>Named <see cref="HttpClient"/> for the Twilio Messages API. Configured by Host DI.</summary>
    public const string TwilioHttpClientName = "CodeFlow.Notifications.Twilio";

    /// <summary>Default base address for the Twilio REST API.</summary>
    public static readonly Uri TwilioDefaultBaseAddress = new("https://api.twilio.com/", UriKind.Absolute);

    private readonly IHttpClientFactory httpClientFactory;
    private readonly ILoggerFactory loggerFactory;
    private readonly TimeProvider clock;

    public SmsNotificationProviderFactory(
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory,
        TimeProvider? clock = null)
    {
        this.httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        this.loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        this.clock = clock ?? TimeProvider.System;
    }

    public NotificationChannel Channel => NotificationChannel.Sms;

    public Task<INotificationProvider> CreateAsync(
        NotificationProviderConfigWithCredential config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        var credentials = TwilioSmsCredentials.Parse(config.PlaintextCredential)
            ?? throw new TwilioSmsCredentialsException(
                $"SMS provider '{config.Config.Id}' has no Twilio credential configured.");

        var httpClient = httpClientFactory.CreateClient(TwilioHttpClientName);
        if (httpClient.BaseAddress is null)
        {
            httpClient.BaseAddress = TwilioDefaultBaseAddress;
        }

        var provider = new TwilioSmsNotificationProvider(
            config,
            credentials,
            httpClient,
            loggerFactory.CreateLogger<TwilioSmsNotificationProvider>(),
            clock);

        return Task.FromResult<INotificationProvider>(provider);
    }
}
