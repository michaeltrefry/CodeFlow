using CodeFlow.Contracts.Notifications;
using CodeFlow.Persistence.Notifications;
using Microsoft.Extensions.Logging;

namespace CodeFlow.Orchestration.Notifications.Providers.Slack;

/// <summary>
/// Builds a fresh <see cref="SlackNotificationProvider"/> per stored Slack configuration row.
/// Resolves a named <see cref="HttpClient"/> from <see cref="IHttpClientFactory"/> so the
/// underlying handler benefits from connection pooling + DNS rotation; the timeout + base
/// address come from <see cref="HttpClientName"/>'s configuration in
/// <c>HostExtensions</c>.
/// </summary>
public sealed class SlackNotificationProviderFactory : INotificationProviderFactory
{
    /// <summary>Named <see cref="HttpClient"/> for Slack Web API calls. Configured by Host DI.</summary>
    public const string HttpClientName = "CodeFlow.Notifications.Slack";

    /// <summary>Default base address for the Slack Web API.</summary>
    public static readonly Uri DefaultBaseAddress = new("https://slack.com/api/", UriKind.Absolute);

    private readonly IHttpClientFactory httpClientFactory;
    private readonly ILoggerFactory loggerFactory;
    private readonly TimeProvider clock;

    public SlackNotificationProviderFactory(
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory,
        TimeProvider? clock = null)
    {
        this.httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        this.loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        this.clock = clock ?? TimeProvider.System;
    }

    public NotificationChannel Channel => NotificationChannel.Slack;

    public Task<INotificationProvider> CreateAsync(
        NotificationProviderConfigWithCredential config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        var httpClient = httpClientFactory.CreateClient(HttpClientName);
        if (httpClient.BaseAddress is null)
        {
            // The named client's BaseAddress may not be set in dev/test setups; default to the
            // canonical Slack endpoint so chat.postMessage / auth.test resolve correctly.
            httpClient.BaseAddress = DefaultBaseAddress;
        }

        var provider = new SlackNotificationProvider(
            config,
            httpClient,
            loggerFactory.CreateLogger<SlackNotificationProvider>(),
            clock);

        return Task.FromResult<INotificationProvider>(provider);
    }
}
