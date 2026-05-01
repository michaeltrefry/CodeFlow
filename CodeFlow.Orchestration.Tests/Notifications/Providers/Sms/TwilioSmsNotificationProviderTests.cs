using System.Net;
using System.Net.Http.Headers;
using CodeFlow.Contracts.Notifications;
using CodeFlow.Orchestration.Notifications.Providers.Sms.Twilio;
using CodeFlow.Persistence.Notifications;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeFlow.Orchestration.Tests.Notifications.Providers.Sms;

public sealed class TwilioSmsNotificationProviderTests
{
    [Fact]
    public async Task SendAsync_On201_ReturnsSentWithSidAsProviderMessageId()
    {
        var handler = new ScriptedHttpHandler();
        handler.OnRequest("Messages.json", req =>
        {
            req.Method.Should().Be(HttpMethod.Post);
            req.Headers.Authorization!.Scheme.Should().Be("Basic");
            // Basic auth header is base64("ACtest:authsecret")
            var decoded = System.Text.Encoding.ASCII.GetString(Convert.FromBase64String(req.Headers.Authorization.Parameter!));
            decoded.Should().Be("ACtest:authsecret");

            var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            body.Should().Contain("To=%2B15559876543");
            body.Should().Contain("From=%2B15551234567");
            body.Should().Contain("Body=Open+the+HITL+task");

            return Json(HttpStatusCode.Created, """
            {
              "sid": "SMabcdef1234567890",
              "status": "queued",
              "from": "+15551234567",
              "to": "+15559876543"
            }
            """);
        });

        var provider = BuildProvider("sms-twilio", "+15551234567", handler);
        var result = await provider.SendAsync(NewMessage("+15559876543", body: "Open the HITL task"), NewRoute("sms-twilio"));

        result.Status.Should().Be(NotificationDeliveryStatus.Sent);
        result.ProviderMessageId.Should().Be("SMabcdef1234567890");
        result.NormalizedDestination.Should().Be("+15559876543");
        result.ErrorCode.Should().BeNull();
    }

    [Fact]
    public async Task SendAsync_On400WithTwilioErrorCode_ReturnsFailedWithNamespacedCode()
    {
        var handler = new ScriptedHttpHandler();
        handler.OnRequest("Messages.json", _ => Json(HttpStatusCode.BadRequest, """
            {
              "code": 21211,
              "message": "The 'To' number +15559876543 is not a valid phone number.",
              "more_info": "https://www.twilio.com/docs/errors/21211",
              "status": 400
            }
            """));

        var provider = BuildProvider("sms-twilio", "+15551234567", handler);
        var result = await provider.SendAsync(NewMessage("+15559876543"), NewRoute("sms-twilio"));

        result.Status.Should().Be(NotificationDeliveryStatus.Failed);
        result.ErrorCode.Should().Be("sms.twilio.21211");
        result.ErrorMessage.Should().Contain("not a valid phone number");
    }

    [Fact]
    public async Task SendAsync_On401_ReturnsUnauthorizedWhenNoTwilioCodeBody()
    {
        var handler = new ScriptedHttpHandler();
        handler.OnRequest("Messages.json", _ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var provider = BuildProvider("sms-twilio", "+15551234567", handler);
        var result = await provider.SendAsync(NewMessage("+15559876543"), NewRoute("sms-twilio"));

        result.Status.Should().Be(NotificationDeliveryStatus.Failed);
        result.ErrorCode.Should().Be("sms.twilio.unauthorized");
    }

    [Fact]
    public async Task SendAsync_OnHttpTransportException_ReturnsTransportErrorCode()
    {
        var handler = new ScriptedHttpHandler();
        handler.OnRequest("Messages.json", _ => throw new HttpRequestException("connection reset"));

        var provider = BuildProvider("sms-twilio", "+15551234567", handler);
        var result = await provider.SendAsync(NewMessage("+15559876543"), NewRoute("sms-twilio"));

        result.Status.Should().Be(NotificationDeliveryStatus.Failed);
        result.ErrorCode.Should().Be("sms.twilio.transport_error");
        result.ErrorMessage.Should().Contain("connection reset");
    }

    [Fact]
    public async Task SendAsync_WhenRecipientsEmpty_ReturnsFailedWithoutTouchingTwilio()
    {
        var handler = new ScriptedHttpHandler();
        var provider = BuildProvider("sms-twilio", "+15551234567", handler);

        var emptyMessage = new NotificationMessage(
            EventId: Guid.NewGuid(),
            EventKind: NotificationEventKind.HitlTaskPending,
            Channel: NotificationChannel.Sms,
            Recipients: Array.Empty<NotificationRecipient>(),
            Body: "anything",
            ActionUrl: new Uri("https://codeflow.example.com/hitl?task=1"),
            Severity: NotificationSeverity.Normal);

        var result = await provider.SendAsync(emptyMessage, NewRoute("sms-twilio"));

        result.Status.Should().Be(NotificationDeliveryStatus.Failed);
        result.ErrorCode.Should().Be("sms.twilio.missing_recipient");
        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task SendAsync_WhenFromAddressMissing_ReturnsFailedWithoutTouchingTwilio()
    {
        var handler = new ScriptedHttpHandler();
        var provider = BuildProvider("sms-twilio", fromAddress: null, handler);

        var result = await provider.SendAsync(NewMessage("+15559876543"), NewRoute("sms-twilio"));

        result.Status.Should().Be(NotificationDeliveryStatus.Failed);
        result.ErrorCode.Should().Be("sms.twilio.missing_from_address");
        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task ValidateAsync_On200_ReturnsValid()
    {
        var handler = new ScriptedHttpHandler();
        handler.OnRequest("ACtest.json", req =>
        {
            req.Method.Should().Be(HttpMethod.Get);
            req.Headers.Authorization!.Scheme.Should().Be("Basic");
            return Json(HttpStatusCode.OK, """{"sid":"ACtest","friendly_name":"Test","status":"active"}""");
        });

        var provider = BuildProvider("sms-twilio", "+15551234567", handler);
        var result = await provider.ValidateAsync();

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateAsync_On401_ReturnsInvalidWithUnauthorizedErrorCode()
    {
        var handler = new ScriptedHttpHandler();
        handler.OnRequest("ACtest.json", _ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var provider = BuildProvider("sms-twilio", "+15551234567", handler);
        var result = await provider.ValidateAsync();

        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be("sms.twilio.unauthorized");
    }

    [Fact]
    public async Task ValidateAsync_OnHttpTransportException_ReturnsTransportErrorCode()
    {
        var handler = new ScriptedHttpHandler();
        handler.OnRequest("ACtest.json", _ => throw new HttpRequestException("dns resolution failed"));

        var provider = BuildProvider("sms-twilio", "+15551234567", handler);
        var result = await provider.ValidateAsync();

        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be("sms.twilio.transport_error");
        result.ErrorMessage.Should().Contain("dns resolution failed");
    }

    private static TwilioSmsNotificationProvider BuildProvider(
        string id,
        string? fromAddress,
        ScriptedHttpHandler handler)
    {
        var config = new NotificationProviderConfigWithCredential(
            Config: new NotificationProviderConfig(
                Id: id,
                DisplayName: $"Twilio {id}",
                Channel: NotificationChannel.Sms,
                EndpointUrl: null,
                FromAddress: fromAddress,
                HasCredential: true,
                AdditionalConfigJson: null,
                Enabled: true,
                IsArchived: false,
                CreatedAtUtc: DateTime.UtcNow,
                CreatedBy: null,
                UpdatedAtUtc: DateTime.UtcNow,
                UpdatedBy: null),
            PlaintextCredential: """{"account_sid":"ACtest","auth_token":"authsecret"}""");

        var credentials = TwilioSmsCredentials.Parse(config.PlaintextCredential)!;
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.twilio.com/") };
        return new TwilioSmsNotificationProvider(
            config,
            credentials,
            httpClient,
            NullLogger<TwilioSmsNotificationProvider>.Instance);
    }

    private static NotificationRoute NewRoute(string providerId) => new(
        RouteId: $"route-{providerId}",
        EventKind: NotificationEventKind.HitlTaskPending,
        ProviderId: providerId,
        Recipients: [new NotificationRecipient(NotificationChannel.Sms, "+15559876543")],
        Template: new NotificationTemplateRef("hitl-task-pending/sms/default", 1));

    private static NotificationMessage NewMessage(string toNumber, string body = "HITL task pending")
    {
        return new NotificationMessage(
            EventId: Guid.NewGuid(),
            EventKind: NotificationEventKind.HitlTaskPending,
            Channel: NotificationChannel.Sms,
            Recipients: [new NotificationRecipient(NotificationChannel.Sms, toNumber)],
            Body: body,
            ActionUrl: new Uri("https://codeflow.example.com/hitl?task=42"),
            Severity: NotificationSeverity.Urgent,
            Subject: null,
            Template: new NotificationTemplateRef("hitl-task-pending/sms/default", 1));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };

    private sealed class ScriptedHttpHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> handlers = new();
        public int RequestCount { get; private set; }

        public void OnRequest(string pathSuffix, Func<HttpRequestMessage, HttpResponseMessage> handler) =>
            handlers[pathSuffix] = handler;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            var pathSuffix = request.RequestUri!.AbsolutePath.Split('/')[^1];
            if (!handlers.TryGetValue(pathSuffix, out var handler))
            {
                throw new InvalidOperationException(
                    $"No scripted handler registered for path suffix '{pathSuffix}'.");
            }
            return Task.FromResult(handler(request));
        }
    }
}
