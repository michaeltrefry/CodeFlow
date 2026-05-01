using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeFlow.Contracts.Notifications;
using CodeFlow.Persistence.Notifications;
using Microsoft.Extensions.Logging;

namespace CodeFlow.Orchestration.Notifications.Providers.Sms.Twilio;

/// <summary>
/// Twilio Messages API provider. POSTs form-urlencoded
/// <c>To/From/Body</c> to <c>https://api.twilio.com/2010-04-01/Accounts/{AccountSid}/Messages.json</c>
/// with HTTP Basic auth. Returns <see cref="NotificationDeliveryStatus.Sent"/> with the
/// Twilio message <c>sid</c> as <see cref="NotificationDeliveryResult.ProviderMessageId"/>;
/// Twilio errors map to <c>sms.twilio.{numeric_code}</c> (e.g. 21211 → "invalid To number").
/// </summary>
public sealed class TwilioSmsNotificationProvider : INotificationProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly NotificationProviderConfigWithCredential config;
    private readonly TwilioSmsCredentials credentials;
    private readonly HttpClient httpClient;
    private readonly ILogger<TwilioSmsNotificationProvider> logger;
    private readonly TimeProvider clock;

    public TwilioSmsNotificationProvider(
        NotificationProviderConfigWithCredential config,
        TwilioSmsCredentials credentials,
        HttpClient httpClient,
        ILogger<TwilioSmsNotificationProvider> logger,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.Config.Channel != NotificationChannel.Sms)
        {
            throw new ArgumentException(
                $"TwilioSmsNotificationProvider requires an Sms channel config; got {config.Config.Channel}.",
                nameof(config));
        }

        this.config = config;
        this.credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.clock = clock ?? TimeProvider.System;
    }

    public string Id => config.Config.Id;

    public NotificationChannel Channel => NotificationChannel.Sms;

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
            return Failed(message, route, attemptedAt, destination,
                "sms.twilio.missing_recipient",
                "NotificationMessage.Recipients was empty; cannot send SMS without a To number.");
        }

        if (string.IsNullOrWhiteSpace(config.Config.FromAddress))
        {
            return Failed(message, route, attemptedAt, destination,
                "sms.twilio.missing_from_address",
                $"Twilio SMS provider '{Id}' has no FromAddress configured.");
        }

        var path = $"2010-04-01/Accounts/{Uri.EscapeDataString(credentials.AccountSid)}/Messages.json";
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("To", destination),
                new KeyValuePair<string, string>("From", config.Config.FromAddress!),
                new KeyValuePair<string, string>("Body", message.Body),
            }),
        };
        request.Headers.Authorization = BuildBasicAuthHeader(credentials);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            return Failed(message, route, attemptedAt, destination, "sms.twilio.transport_error", ex.Message);
        }
        catch (TaskCanceledException ex)
        {
            return Failed(message, route, attemptedAt, destination, "sms.twilio.timeout", ex.Message);
        }

        try
        {
            if (response.IsSuccessStatusCode)
            {
                var ok = await response.Content.ReadFromJsonAsync<TwilioMessageResponse>(JsonOptions, cancellationToken);
                if (ok is null || string.IsNullOrEmpty(ok.Sid))
                {
                    return Failed(message, route, attemptedAt, destination,
                        "sms.twilio.empty_response",
                        $"Twilio returned HTTP {(int)response.StatusCode} but no message sid.");
                }

                return new NotificationDeliveryResult(
                    EventId: message.EventId,
                    RouteId: route.RouteId,
                    ProviderId: Id,
                    Status: NotificationDeliveryStatus.Sent,
                    AttemptedAtUtc: attemptedAt,
                    CompletedAtUtc: clock.GetUtcNow(),
                    AttemptNumber: 1,
                    NormalizedDestination: destination,
                    ProviderMessageId: ok.Sid);
            }

            // Error path — Twilio returns a JSON body with `code` and `message`. Fall back to
            // the HTTP status when the body is missing or malformed.
            TwilioErrorResponse? error = null;
            try
            {
                error = await response.Content.ReadFromJsonAsync<TwilioErrorResponse>(JsonOptions, cancellationToken);
            }
            catch (JsonException)
            {
                // Body wasn't JSON; fall through to the HTTP-status fallback below.
            }

            string errorCode;
            if (error is { Code: > 0 })
            {
                errorCode = $"sms.twilio.{error.Code.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            }
            else if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                errorCode = "sms.twilio.unauthorized";
            }
            else
            {
                errorCode = $"sms.twilio.http_{(int)response.StatusCode}";
            }

            return Failed(message, route, attemptedAt, destination, errorCode, error?.Message);
        }
        finally
        {
            response.Dispose();
        }
    }

    public async Task<ProviderValidationResult> ValidateAsync(CancellationToken cancellationToken = default)
    {
        // GET /2010-04-01/Accounts/{AccountSid}.json — 200 means the account exists and the
        // auth token authorised the request. Doesn't send a real SMS, so it's safe for the
        // admin "test connection" path.
        var path = $"2010-04-01/Accounts/{Uri.EscapeDataString(credentials.AccountSid)}.json";
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = BuildBasicAuthHeader(credentials);

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return ProviderValidationResult.Valid();
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return ProviderValidationResult.Invalid("sms.twilio.unauthorized",
                    "Twilio rejected the account_sid / auth_token pair.");
            }

            TwilioErrorResponse? error = null;
            try
            {
                error = await response.Content.ReadFromJsonAsync<TwilioErrorResponse>(JsonOptions, cancellationToken);
            }
            catch (JsonException)
            {
                // ignore — fall through to HTTP-status code
            }

            var code = error is { Code: > 0 }
                ? $"sms.twilio.{error.Code.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                : $"sms.twilio.http_{(int)response.StatusCode}";

            return ProviderValidationResult.Invalid(code,
                error?.Message ?? $"Twilio account lookup returned HTTP {(int)response.StatusCode}.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex,
                "Twilio SMS provider '{ProviderId}' transport error during ValidateAsync.", Id);
            return ProviderValidationResult.Invalid("sms.twilio.transport_error", ex.Message);
        }
        catch (TaskCanceledException ex)
        {
            return ProviderValidationResult.Invalid("sms.twilio.timeout", ex.Message);
        }
    }

    private static AuthenticationHeaderValue BuildBasicAuthHeader(TwilioSmsCredentials creds)
    {
        var raw = $"{creds.AccountSid}:{creds.AuthToken}";
        var encoded = Convert.ToBase64String(Encoding.ASCII.GetBytes(raw));
        return new AuthenticationHeaderValue("Basic", encoded);
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
            "Twilio SMS provider '{ProviderId}' delivery failed for event {EventId} → {Destination}: {ErrorCode} {ErrorMessage}",
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

    private sealed class TwilioMessageResponse
    {
        [JsonPropertyName("sid")] public string? Sid { get; init; }
        [JsonPropertyName("status")] public string? Status { get; init; }
    }

    private sealed class TwilioErrorResponse
    {
        [JsonPropertyName("code")] public int? Code { get; init; }
        [JsonPropertyName("message")] public string? Message { get; init; }
        [JsonPropertyName("more_info")] public string? MoreInfo { get; init; }
    }
}
