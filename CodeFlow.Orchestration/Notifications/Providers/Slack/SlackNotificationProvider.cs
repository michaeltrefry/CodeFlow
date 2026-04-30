using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using CodeFlow.Contracts.Notifications;
using CodeFlow.Persistence.Notifications;
using Microsoft.Extensions.Logging;

namespace CodeFlow.Orchestration.Notifications.Providers.Slack;

/// <summary>
/// Bot-token Slack provider. Sends a single channel message per call to
/// <see cref="SendAsync"/> via <c>chat.postMessage</c>. Each instance binds to one
/// configuration row (one workspace + bot token); multiple Slack workspaces fan out via
/// multiple configuration rows handled by <see cref="SlackNotificationProviderFactory"/>.
/// </summary>
public sealed class SlackNotificationProvider : INotificationProvider
{
    private const string PostMessagePath = "chat.postMessage";
    private const string AuthTestPath = "auth.test";

    private readonly NotificationProviderConfigWithCredential config;
    private readonly HttpClient httpClient;
    private readonly ILogger<SlackNotificationProvider> logger;
    private readonly TimeProvider clock;

    public SlackNotificationProvider(
        NotificationProviderConfigWithCredential config,
        HttpClient httpClient,
        ILogger<SlackNotificationProvider> logger,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.Config.Channel != NotificationChannel.Slack)
        {
            throw new ArgumentException(
                $"SlackNotificationProvider requires a Slack channel config; got {config.Config.Channel}.",
                nameof(config));
        }

        this.config = config;
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.clock = clock ?? TimeProvider.System;
    }

    public string Id => config.Config.Id;

    public NotificationChannel Channel => NotificationChannel.Slack;

    public async Task<NotificationDeliveryResult> SendAsync(
        NotificationMessage message,
        NotificationRoute route,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(route);

        var attemptedAt = clock.GetUtcNow();
        var recipient = message.Recipients.Count > 0 ? message.Recipients[0] : null;
        var destination = recipient?.Address ?? string.Empty;

        if (string.IsNullOrWhiteSpace(destination))
        {
            return Failed(message, route, attemptedAt, destination, "slack.missing_recipient",
                "NotificationMessage.Recipients was empty; cannot post to Slack without a channel id.");
        }

        if (string.IsNullOrEmpty(config.PlaintextCredential))
        {
            return Failed(message, route, attemptedAt, destination, "slack.missing_credential",
                $"Slack provider '{Id}' has no bot token configured.");
        }

        var payload = new SlackPostMessageRequest
        {
            Channel = destination,
            Text = ComposeText(message),
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, PostMessagePath)
        {
            Content = JsonContent.Create(payload, options: SlackJsonOptions.Default),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.PlaintextCredential);

        SlackPostMessageResponse? response;
        try
        {
            using var httpResponse = await httpClient.SendAsync(request, cancellationToken);
            response = await httpResponse.Content.ReadFromJsonAsync<SlackPostMessageResponse>(
                SlackJsonOptions.Default,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            return Failed(message, route, attemptedAt, destination, "slack.transport_error", ex.Message);
        }
        catch (TaskCanceledException ex)
        {
            return Failed(message, route, attemptedAt, destination, "slack.timeout", ex.Message);
        }

        if (response is null)
        {
            return Failed(message, route, attemptedAt, destination, "slack.empty_response",
                "Slack returned an empty body.");
        }

        if (!response.Ok)
        {
            var errorCode = string.IsNullOrEmpty(response.Error)
                ? "slack.unknown_error"
                : $"slack.{response.Error}";
            return Failed(message, route, attemptedAt, destination, errorCode, response.Error);
        }

        return new NotificationDeliveryResult(
            EventId: message.EventId,
            RouteId: route.RouteId,
            ProviderId: Id,
            Status: NotificationDeliveryStatus.Sent,
            AttemptedAtUtc: attemptedAt,
            CompletedAtUtc: clock.GetUtcNow(),
            AttemptNumber: 1,
            NormalizedDestination: response.Channel ?? destination,
            ProviderMessageId: response.Ts);
    }

    public async Task<ProviderValidationResult> ValidateAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(config.PlaintextCredential))
        {
            return ProviderValidationResult.Invalid("slack.missing_credential",
                $"Slack provider '{Id}' has no bot token configured.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, AuthTestPath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.PlaintextCredential);

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadFromJsonAsync<SlackAuthTestResponse>(
                SlackJsonOptions.Default,
                cancellationToken);

            if (body is null)
            {
                return ProviderValidationResult.Invalid("slack.empty_response",
                    "Slack auth.test returned an empty body.");
            }

            if (!body.Ok)
            {
                var errorCode = string.IsNullOrEmpty(body.Error)
                    ? "slack.unknown_error"
                    : $"slack.{body.Error}";
                return ProviderValidationResult.Invalid(errorCode, body.Error ?? "Slack auth.test returned ok=false without an error code.");
            }

            return ProviderValidationResult.Valid();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex,
                "Slack provider '{ProviderId}' transport error while validating bot token.", Id);
            return ProviderValidationResult.Invalid("slack.transport_error", ex.Message);
        }
        catch (TaskCanceledException ex)
        {
            return ProviderValidationResult.Invalid("slack.timeout", ex.Message);
        }
    }

    private NotificationDeliveryResult Failed(
        NotificationMessage message,
        NotificationRoute route,
        DateTimeOffset attemptedAt,
        string destination,
        string errorCode,
        string? errorMessage)
    {
        logger.LogWarning(
            "Slack provider '{ProviderId}' delivery failed for event {EventId} → {Destination}: {ErrorCode} {ErrorMessage}",
            Id, message.EventId, destination, errorCode, errorMessage);

        return new NotificationDeliveryResult(
            EventId: message.EventId,
            RouteId: route.RouteId,
            ProviderId: Id,
            Status: NotificationDeliveryStatus.Failed,
            AttemptedAtUtc: attemptedAt,
            CompletedAtUtc: clock.GetUtcNow(),
            AttemptNumber: 1,
            NormalizedDestination: destination,
            ProviderMessageId: null,
            ErrorCode: errorCode,
            ErrorMessage: errorMessage);
    }

    private static string ComposeText(NotificationMessage message)
    {
        // Subject is rendered into a bold header when present; templates that already embed
        // an action URL in the body are left untouched (sc-52's renderer threads the URL via
        // {{ action_url }}, so the body usually carries the link already).
        if (string.IsNullOrEmpty(message.Subject))
        {
            return message.Body;
        }

        return $"*{message.Subject}*\n{message.Body}";
    }
}

internal sealed class SlackPostMessageRequest
{
    [JsonPropertyName("channel")]
    public string Channel { get; init; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;
}

internal sealed class SlackPostMessageResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("channel")]
    public string? Channel { get; init; }

    [JsonPropertyName("ts")]
    public string? Ts { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

internal sealed class SlackAuthTestResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}
