using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CodeFlow.Contracts.Notifications;
using CodeFlow.Orchestration.Notifications.Providers.Slack;
using CodeFlow.Persistence.Notifications;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeFlow.Orchestration.Tests.Notifications.Providers.Slack;

public sealed class SlackNotificationProviderTests
{
    [Fact]
    public async Task SendAsync_OnSlackOkResponse_ReturnsSentWithProviderMessageId()
    {
        var handler = new ScriptedHttpHandler();
        handler.OnPost("chat.postMessage", req =>
        {
            req.Headers.Authorization!.Scheme.Should().Be("Bearer");
            req.Headers.Authorization.Parameter.Should().Be("xoxb-test-token");

            var body = req.Content!.ReadFromJsonAsync<JsonElement>().GetAwaiter().GetResult();
            body.GetProperty("channel").GetString().Should().Be("C012AB3CD");
            body.GetProperty("text").GetString().Should().Contain("HITL review");

            return SlackResponse(200, """
            {
              "ok": true,
              "channel": "C012AB3CD",
              "ts": "1714521234.000200",
              "message": { "text": "..." }
            }
            """);
        });

        var provider = BuildProvider("slack-prod", "xoxb-test-token", handler);
        var route = NewRoute("slack-prod");
        var message = NewMessage("C012AB3CD", subject: "HITL review needed", body: "Open: https://codeflow.example.com/hitl?task=42");

        var result = await provider.SendAsync(message, route);

        result.Status.Should().Be(NotificationDeliveryStatus.Sent);
        result.ProviderId.Should().Be("slack-prod");
        result.RouteId.Should().Be(route.RouteId);
        result.NormalizedDestination.Should().Be("C012AB3CD");
        result.ProviderMessageId.Should().Be("1714521234.000200");
        result.ErrorCode.Should().BeNull();
        handler.PostCount.Should().Be(1);
    }

    [Fact]
    public async Task SendAsync_OnSlackErrorResponse_ReturnsFailedWithNamespacedErrorCode()
    {
        var handler = new ScriptedHttpHandler();
        handler.OnPost("chat.postMessage", _ => SlackResponse(200, """
            { "ok": false, "error": "channel_not_found" }
            """));

        var provider = BuildProvider("slack-prod", "xoxb-test-token", handler);
        var result = await provider.SendAsync(NewMessage("C-bad-channel"), NewRoute("slack-prod"));

        result.Status.Should().Be(NotificationDeliveryStatus.Failed);
        result.ErrorCode.Should().Be("slack.channel_not_found");
        result.ErrorMessage.Should().Be("channel_not_found");
        result.ProviderMessageId.Should().BeNull();
        result.NormalizedDestination.Should().Be("C-bad-channel");
    }

    [Fact]
    public async Task SendAsync_OnHttpTransportException_ReturnsFailedWithTransportErrorCode()
    {
        var handler = new ScriptedHttpHandler();
        handler.OnPost("chat.postMessage", _ => throw new HttpRequestException("connection reset"));

        var provider = BuildProvider("slack-prod", "xoxb-test-token", handler);
        var result = await provider.SendAsync(NewMessage("C012"), NewRoute("slack-prod"));

        result.Status.Should().Be(NotificationDeliveryStatus.Failed);
        result.ErrorCode.Should().Be("slack.transport_error");
        result.ErrorMessage.Should().Contain("connection reset");
    }

    [Fact]
    public async Task SendAsync_WhenCredentialMissing_DoesNotCallSlackAndReturnsFailed()
    {
        var handler = new ScriptedHttpHandler();
        var provider = BuildProvider("slack-prod", credential: null, handler);

        var result = await provider.SendAsync(NewMessage("C012"), NewRoute("slack-prod"));

        result.Status.Should().Be(NotificationDeliveryStatus.Failed);
        result.ErrorCode.Should().Be("slack.missing_credential");
        handler.PostCount.Should().Be(0, "must not call Slack without a bot token");
    }

    [Fact]
    public async Task SendAsync_WhenRecipientsEmpty_ReturnsFailedWithoutTouchingSlack()
    {
        var handler = new ScriptedHttpHandler();
        var provider = BuildProvider("slack-prod", "xoxb-test-token", handler);

        var emptyMessage = new NotificationMessage(
            EventId: Guid.NewGuid(),
            EventKind: NotificationEventKind.HitlTaskPending,
            Channel: NotificationChannel.Slack,
            Recipients: Array.Empty<NotificationRecipient>(),
            Body: "anything",
            ActionUrl: new Uri("https://codeflow.example.com/hitl?task=1"),
            Severity: NotificationSeverity.Normal);

        var result = await provider.SendAsync(emptyMessage, NewRoute("slack-prod"));

        result.Status.Should().Be(NotificationDeliveryStatus.Failed);
        result.ErrorCode.Should().Be("slack.missing_recipient");
        handler.PostCount.Should().Be(0);
    }

    [Fact]
    public async Task SendAsync_PrependsSubjectAsBoldHeaderWhenPresent()
    {
        string? sentText = null;
        var handler = new ScriptedHttpHandler();
        handler.OnPost("chat.postMessage", req =>
        {
            var body = req.Content!.ReadFromJsonAsync<JsonElement>().GetAwaiter().GetResult();
            sentText = body.GetProperty("text").GetString();
            return SlackResponse(200, """{"ok": true, "channel": "C012", "ts": "1.0"}""");
        });

        var provider = BuildProvider("slack-prod", "xoxb-test-token", handler);
        await provider.SendAsync(
            NewMessage("C012", subject: "[CodeFlow] HITL review needed", body: "Open: https://x"),
            NewRoute("slack-prod"));

        sentText.Should().Be("*[CodeFlow] HITL review needed*\nOpen: https://x");
    }

    [Fact]
    public async Task SendAsync_WhenSubjectMissing_SendsBodyOnly()
    {
        string? sentText = null;
        var handler = new ScriptedHttpHandler();
        handler.OnPost("chat.postMessage", req =>
        {
            var body = req.Content!.ReadFromJsonAsync<JsonElement>().GetAwaiter().GetResult();
            sentText = body.GetProperty("text").GetString();
            return SlackResponse(200, """{"ok": true, "channel": "C012", "ts": "1.0"}""");
        });

        var provider = BuildProvider("slack-prod", "xoxb-test-token", handler);
        await provider.SendAsync(
            NewMessage("C012", subject: null, body: "plain body"),
            NewRoute("slack-prod"));

        sentText.Should().Be("plain body");
    }

    [Fact]
    public async Task ValidateAsync_OnAuthTestOk_ReturnsValid()
    {
        var handler = new ScriptedHttpHandler();
        handler.OnPost("auth.test", req =>
        {
            req.Headers.Authorization!.Parameter.Should().Be("xoxb-test-token");
            return SlackResponse(200, """{"ok": true, "url": "https://acme.slack.com/", "team": "acme"}""");
        });

        var provider = BuildProvider("slack-prod", "xoxb-test-token", handler);
        var result = await provider.ValidateAsync();

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateAsync_OnAuthTestError_ReturnsInvalidWithNamespacedErrorCode()
    {
        var handler = new ScriptedHttpHandler();
        handler.OnPost("auth.test", _ => SlackResponse(200,
            """{"ok": false, "error": "invalid_auth"}"""));

        var provider = BuildProvider("slack-prod", "xoxb-test-token", handler);
        var result = await provider.ValidateAsync();

        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be("slack.invalid_auth");
        result.ErrorMessage.Should().Be("invalid_auth");
    }

    [Fact]
    public async Task ValidateAsync_WhenCredentialMissing_DoesNotCallSlackAndReturnsInvalid()
    {
        var handler = new ScriptedHttpHandler();
        var provider = BuildProvider("slack-prod", credential: null, handler);
        var result = await provider.ValidateAsync();

        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be("slack.missing_credential");
        handler.PostCount.Should().Be(0);
    }

    // --- helpers --------------------------------------------------------------------------

    private static SlackNotificationProvider BuildProvider(
        string id,
        string? credential,
        ScriptedHttpHandler handler)
    {
        var config = new NotificationProviderConfigWithCredential(
            Config: new NotificationProviderConfig(
                Id: id,
                DisplayName: $"Slack {id}",
                Channel: NotificationChannel.Slack,
                EndpointUrl: null,
                FromAddress: null,
                HasCredential: credential is not null,
                AdditionalConfigJson: null,
                Enabled: true,
                IsArchived: false,
                CreatedAtUtc: DateTime.UtcNow,
                CreatedBy: null,
                UpdatedAtUtc: DateTime.UtcNow,
                UpdatedBy: null),
            PlaintextCredential: credential);

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://slack.com/api/") };
        return new SlackNotificationProvider(
            config,
            httpClient,
            NullLogger<SlackNotificationProvider>.Instance);
    }

    private static NotificationRoute NewRoute(string providerId)
    {
        return new NotificationRoute(
            RouteId: "route-" + providerId,
            EventKind: NotificationEventKind.HitlTaskPending,
            ProviderId: providerId,
            Recipients: [new NotificationRecipient(NotificationChannel.Slack, "C012AB3CD")],
            Template: new NotificationTemplateRef("hitl-task-pending/slack/default", 1));
    }

    private static NotificationMessage NewMessage(
        string destinationChannelId,
        string? subject = "HITL review needed",
        string body = "A HITL task is pending.")
    {
        return new NotificationMessage(
            EventId: Guid.NewGuid(),
            EventKind: NotificationEventKind.HitlTaskPending,
            Channel: NotificationChannel.Slack,
            Recipients: [new NotificationRecipient(NotificationChannel.Slack, destinationChannelId)],
            Body: body,
            ActionUrl: new Uri("https://codeflow.example.com/hitl?task=42"),
            Severity: NotificationSeverity.High,
            Subject: subject,
            Template: new NotificationTemplateRef("hitl-task-pending/slack/default", 1));
    }

    private static HttpResponseMessage SlackResponse(int status, string json) =>
        new((HttpStatusCode)status)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };

    private sealed class ScriptedHttpHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> handlers = new();
        public int PostCount { get; private set; }

        public void OnPost(string pathSuffix, Func<HttpRequestMessage, HttpResponseMessage> handler) =>
            handlers[pathSuffix] = handler;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            PostCount++;
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
