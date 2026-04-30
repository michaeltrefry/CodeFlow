using CodeFlow.Contracts.Notifications;
using CodeFlow.Orchestration.Notifications;
using CodeFlow.Persistence.Notifications;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeFlow.Orchestration.Tests.Notifications;

public sealed class NotificationDispatcherTests
{
    [Fact]
    public async Task DispatchAsync_NoMatchingRoutes_ReturnsEmpty()
    {
        var dispatcher = BuildDispatcher(new TestHarness());
        var result = await dispatcher.DispatchAsync(SamplePendingEvent());
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task DispatchAsync_FansOutAcrossRoutesAndRecipients_RecordingOneAttemptPerPair()
    {
        var harness = new TestHarness();
        var slack = harness.RegisterProvider("slack-prod", NotificationChannel.Slack);
        var email = harness.RegisterProvider("email-mailgun", NotificationChannel.Email);

        harness.AddTemplate("hitl-task-pending/slack/default", 1, "Body slack {{ event_id }}", "Subj slack");
        harness.AddTemplate("hitl-task-pending/email/default", 1, "Body email {{ event_id }}", "Subj email");

        harness.AddRoute(new NotificationRoute(
            RouteId: "route-slack",
            EventKind: NotificationEventKind.HitlTaskPending,
            ProviderId: "slack-prod",
            Recipients:
            [
                new NotificationRecipient(NotificationChannel.Slack, "C012", "#hitl"),
                new NotificationRecipient(NotificationChannel.Slack, "C999", "#oncall")
            ],
            Template: new NotificationTemplateRef("hitl-task-pending/slack/default", 1)));

        harness.AddRoute(new NotificationRoute(
            RouteId: "route-email",
            EventKind: NotificationEventKind.HitlTaskPending,
            ProviderId: "email-mailgun",
            Recipients: [new NotificationRecipient(NotificationChannel.Email, "ops@example.com")],
            Template: new NotificationTemplateRef("hitl-task-pending/email/default", 1)));

        var dispatcher = BuildDispatcher(harness);
        var evt = SamplePendingEvent();

        var results = await dispatcher.DispatchAsync(evt);

        results.Should().HaveCount(3);
        results.Should().OnlyContain(r => r.Status == NotificationDeliveryStatus.Sent);
        slack.SentMessages.Should().HaveCount(2);
        email.SentMessages.Should().HaveCount(1);

        // Each Sent message carries the source event id and the canonical action url verbatim.
        slack.SentMessages[0].EventId.Should().Be(evt.EventId);
        slack.SentMessages[0].ActionUrl.Should().Be(evt.ActionUrl);
        slack.SentMessages[0].Body.Should().Contain(evt.EventId.ToString());
        slack.SentMessages[0].Subject.Should().Be("Subj slack");

        // Audit trail mirrors what was sent — the persistence layer (sc-51) is the system of
        // record so the dispatcher MUST persist whatever the provider reported.
        harness.AttemptRepository.Recorded.Should().HaveCount(3);
        harness.AttemptRepository.Recorded.Should()
            .OnlyContain(a => a.Result.Status == NotificationDeliveryStatus.Sent && a.Kind == NotificationEventKind.HitlTaskPending);
    }

    [Fact]
    public async Task DispatchAsync_RouteSeverityAboveEvent_SkipsRouteAndRecordsNothing()
    {
        var harness = new TestHarness();
        var sms = harness.RegisterProvider("sms-twilio", NotificationChannel.Sms);
        harness.AddTemplate("tmpl/sms", 1, "Body", null);

        harness.AddRoute(new NotificationRoute(
            RouteId: "route-sms-urgent-only",
            EventKind: NotificationEventKind.HitlTaskPending,
            ProviderId: "sms-twilio",
            Recipients: [new NotificationRecipient(NotificationChannel.Sms, "+15550001111")],
            Template: new NotificationTemplateRef("tmpl/sms", 1),
            MinimumSeverity: NotificationSeverity.Urgent));

        var dispatcher = BuildDispatcher(harness);

        var results = await dispatcher.DispatchAsync(SamplePendingEvent(NotificationSeverity.Normal));

        results.Should().BeEmpty();
        sms.SentMessages.Should().BeEmpty();
        harness.AttemptRepository.Recorded.Should().BeEmpty();
    }

    [Fact]
    public async Task DispatchAsync_PreviousSentAttempt_SkipsAndRecordsSkippedAudit()
    {
        var harness = new TestHarness();
        var slack = harness.RegisterProvider("slack-prod", NotificationChannel.Slack);
        harness.AddTemplate("tmpl/slack", 1, "Body", null);

        var route = new NotificationRoute(
            RouteId: "route-slack",
            EventKind: NotificationEventKind.HitlTaskPending,
            ProviderId: "slack-prod",
            Recipients: [new NotificationRecipient(NotificationChannel.Slack, "C012", "#hitl")],
            Template: new NotificationTemplateRef("tmpl/slack", 1));
        harness.AddRoute(route);

        var evt = SamplePendingEvent();

        // Pre-seed a Sent attempt so the dedupe path triggers.
        await harness.AttemptRepository.RecordAsync(
            new NotificationDeliveryResult(
                EventId: evt.EventId,
                RouteId: "route-slack",
                ProviderId: "slack-prod",
                Status: NotificationDeliveryStatus.Sent,
                AttemptedAtUtc: DateTimeOffset.UtcNow.AddSeconds(-5),
                CompletedAtUtc: DateTimeOffset.UtcNow.AddSeconds(-5),
                AttemptNumber: 1,
                NormalizedDestination: "C012",
                ProviderMessageId: "ts-123"),
            NotificationEventKind.HitlTaskPending);

        var dispatcher = BuildDispatcher(harness);
        var results = await dispatcher.DispatchAsync(evt);

        results.Should().ContainSingle();
        results[0].Status.Should().Be(NotificationDeliveryStatus.Skipped);
        results[0].AttemptNumber.Should().Be(2);
        results[0].ErrorCode.Should().Be("dispatcher.dedupe_already_sent");
        slack.SentMessages.Should().BeEmpty("provider must not be invoked when dedupe matches");
    }

    [Fact]
    public async Task DispatchAsync_ProviderThrows_RecordsFailedAndContinuesFanOut()
    {
        var harness = new TestHarness();
        harness.RegisterProvider("slack-prod", NotificationChannel.Slack, throwOnSend: true);
        var email = harness.RegisterProvider("email-mailgun", NotificationChannel.Email);

        harness.AddTemplate("tmpl/slack", 1, "Body slack", null);
        harness.AddTemplate("tmpl/email", 1, "Body email", null);

        harness.AddRoute(new NotificationRoute(
            RouteId: "route-slack",
            EventKind: NotificationEventKind.HitlTaskPending,
            ProviderId: "slack-prod",
            Recipients: [new NotificationRecipient(NotificationChannel.Slack, "C012")],
            Template: new NotificationTemplateRef("tmpl/slack", 1)));

        harness.AddRoute(new NotificationRoute(
            RouteId: "route-email",
            EventKind: NotificationEventKind.HitlTaskPending,
            ProviderId: "email-mailgun",
            Recipients: [new NotificationRecipient(NotificationChannel.Email, "ops@example.com")],
            Template: new NotificationTemplateRef("tmpl/email", 1)));

        var dispatcher = BuildDispatcher(harness);
        var results = await dispatcher.DispatchAsync(SamplePendingEvent());

        results.Should().HaveCount(2);
        results.Should().Contain(r => r.RouteId == "route-slack" && r.Status == NotificationDeliveryStatus.Failed && r.ErrorCode == "dispatcher.provider_threw");
        results.Should().Contain(r => r.RouteId == "route-email" && r.Status == NotificationDeliveryStatus.Sent);
        email.SentMessages.Should().HaveCount(1, "the email route must still fire when slack throws");
    }

    [Fact]
    public async Task DispatchAsync_ProviderNotRegistered_RecordsFailedAuditAndContinues()
    {
        var harness = new TestHarness();
        var email = harness.RegisterProvider("email-mailgun", NotificationChannel.Email);
        harness.AddTemplate("tmpl/email", 1, "Body email", null);
        harness.AddTemplate("tmpl/slack", 1, "Body slack", null);

        harness.AddRoute(new NotificationRoute(
            RouteId: "route-typo",
            EventKind: NotificationEventKind.HitlTaskPending,
            ProviderId: "slack-typo-not-registered",
            Recipients: [new NotificationRecipient(NotificationChannel.Slack, "C012")],
            Template: new NotificationTemplateRef("tmpl/slack", 1)));

        harness.AddRoute(new NotificationRoute(
            RouteId: "route-email",
            EventKind: NotificationEventKind.HitlTaskPending,
            ProviderId: "email-mailgun",
            Recipients: [new NotificationRecipient(NotificationChannel.Email, "ops@example.com")],
            Template: new NotificationTemplateRef("tmpl/email", 1)));

        var dispatcher = BuildDispatcher(harness);
        var results = await dispatcher.DispatchAsync(SamplePendingEvent());

        results.Should().HaveCount(2);
        var typoResult = results.Should().ContainSingle(r => r.RouteId == "route-typo").Which;
        typoResult.Status.Should().Be(NotificationDeliveryStatus.Failed);
        typoResult.ErrorCode.Should().Be("dispatcher.provider_not_registered");
        email.SentMessages.Should().HaveCount(1);
    }

    [Fact]
    public async Task DispatchAsync_TemplateMissing_RecordsFailedForEveryRecipient()
    {
        var harness = new TestHarness();
        harness.RegisterProvider("slack-prod", NotificationChannel.Slack);

        harness.AddRoute(new NotificationRoute(
            RouteId: "route-slack",
            EventKind: NotificationEventKind.HitlTaskPending,
            ProviderId: "slack-prod",
            Recipients:
            [
                new NotificationRecipient(NotificationChannel.Slack, "C012"),
                new NotificationRecipient(NotificationChannel.Slack, "C999")
            ],
            Template: new NotificationTemplateRef("tmpl/slack-missing", 1)));

        var dispatcher = BuildDispatcher(harness);
        var results = await dispatcher.DispatchAsync(SamplePendingEvent());

        results.Should().HaveCount(2);
        results.Should().OnlyContain(r => r.Status == NotificationDeliveryStatus.Failed
            && r.ErrorCode == "dispatcher.template_not_found");
        results.Select(r => r.NormalizedDestination).Should().BeEquivalentTo(new[] { "C012", "C999" });
    }

    [Fact]
    public async Task DispatchAsync_ProviderReturnsFailure_PersistsProviderErrorVerbatim()
    {
        var harness = new TestHarness();
        var slack = harness.RegisterProvider("slack-prod", NotificationChannel.Slack);
        slack.NextResult = new NotificationDeliveryResult(
            EventId: Guid.Empty, // dispatcher overrides
            RouteId: "ignored-by-dispatcher",
            ProviderId: "slack-prod",
            Status: NotificationDeliveryStatus.Failed,
            AttemptedAtUtc: DateTimeOffset.UtcNow,
            CompletedAtUtc: DateTimeOffset.UtcNow.AddMilliseconds(40),
            AttemptNumber: 99,
            NormalizedDestination: "C012-normalized",
            ProviderMessageId: null,
            ErrorCode: "slack.channel_archived",
            ErrorMessage: "channel is archived");

        harness.AddTemplate("tmpl/slack", 1, "Body", null);
        harness.AddRoute(new NotificationRoute(
            RouteId: "route-slack",
            EventKind: NotificationEventKind.HitlTaskPending,
            ProviderId: "slack-prod",
            Recipients: [new NotificationRecipient(NotificationChannel.Slack, "C012")],
            Template: new NotificationTemplateRef("tmpl/slack", 1)));

        var dispatcher = BuildDispatcher(harness);
        var results = await dispatcher.DispatchAsync(SamplePendingEvent());

        results.Should().ContainSingle();
        var result = results[0];
        result.Status.Should().Be(NotificationDeliveryStatus.Failed);
        result.ErrorCode.Should().Be("slack.channel_archived");
        result.ErrorMessage.Should().Be("channel is archived");
        result.RouteId.Should().Be("route-slack", "dispatcher overrides provider's route id with the route it dispatched on");
        result.NormalizedDestination.Should().Be("C012-normalized");
        result.AttemptNumber.Should().Be(1);
    }

    private static HitlTaskPendingEvent SamplePendingEvent(NotificationSeverity severity = NotificationSeverity.High)
    {
        return new HitlTaskPendingEvent(
            EventId: Guid.NewGuid(),
            OccurredAtUtc: DateTimeOffset.UtcNow,
            ActionUrl: new Uri("https://codeflow.example.com/hitl/4242"),
            Severity: severity,
            HitlTaskId: 4242,
            TraceId: Guid.NewGuid(),
            RoundId: Guid.NewGuid(),
            NodeId: Guid.NewGuid(),
            WorkflowKey: "review-loop-v3",
            WorkflowVersion: 7,
            AgentKey: "reviewer",
            AgentVersion: 2,
            HitlTaskCreatedAtUtc: DateTimeOffset.UtcNow);
    }

    private static NotificationDispatcher BuildDispatcher(TestHarness harness) => new(
        harness.RouteRepository,
        harness.ProviderRegistry,
        harness.Renderer,
        harness.AttemptRepository,
        NullLogger<NotificationDispatcher>.Instance);

    // --- harness types --------------------------------------------------------------------

    private sealed class TestHarness
    {
        public InMemoryRouteRepository RouteRepository { get; } = new();
        public InMemoryAttemptRepository AttemptRepository { get; } = new();
        public StubTemplateRenderer Renderer { get; } = new();
        public List<RecordingProvider> Providers { get; } = new();

        public INotificationProviderRegistry ProviderRegistry => new NotificationProviderRegistry(Providers);

        public RecordingProvider RegisterProvider(
            string id,
            NotificationChannel channel,
            bool throwOnSend = false)
        {
            var provider = new RecordingProvider(id, channel, throwOnSend);
            Providers.Add(provider);
            return provider;
        }

        public void AddRoute(NotificationRoute route) => RouteRepository.Routes.Add(route);

        public void AddTemplate(string id, int version, string body, string? subject) =>
            Renderer.Templates[(id, version)] = (body, subject);
    }

    private sealed class InMemoryRouteRepository : INotificationRouteRepository
    {
        public List<NotificationRoute> Routes { get; } = new();

        public Task<IReadOnlyList<NotificationRoute>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<NotificationRoute>>(Routes.ToArray());

        public Task<NotificationRoute?> GetAsync(string routeId, CancellationToken ct = default) =>
            Task.FromResult(Routes.FirstOrDefault(r => r.RouteId == routeId));

        public Task<IReadOnlyList<NotificationRoute>> ListByEventKindAsync(
            NotificationEventKind eventKind,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<NotificationRoute>>(
                Routes.Where(r => r.EventKind == eventKind && r.Enabled)
                    .OrderBy(r => r.RouteId, StringComparer.Ordinal)
                    .ToArray());

        public Task UpsertAsync(NotificationRoute route, CancellationToken ct = default) => throw new NotSupportedException();

        public Task DeleteAsync(string routeId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class InMemoryAttemptRepository : INotificationDeliveryAttemptRepository
    {
        public List<(NotificationDeliveryResult Result, NotificationEventKind Kind)> Recorded { get; } = new();

        public Task RecordAsync(
            NotificationDeliveryResult result,
            NotificationEventKind eventKind,
            CancellationToken ct = default)
        {
            Recorded.Add((result, eventKind));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<NotificationDeliveryResult>> ListByEventIdAsync(
            Guid eventId,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<NotificationDeliveryResult>>(
                Recorded.Where(r => r.Result.EventId == eventId).Select(r => r.Result).ToArray());

        public Task<NotificationDeliveryResult?> LatestForDestinationAsync(
            Guid eventId,
            string providerId,
            string normalizedDestination,
            CancellationToken ct = default)
        {
            var match = Recorded
                .Where(r => r.Result.EventId == eventId
                    && r.Result.ProviderId == providerId
                    && r.Result.NormalizedDestination == normalizedDestination)
                .OrderByDescending(r => r.Result.AttemptNumber)
                .Select(r => r.Result)
                .FirstOrDefault();
            return Task.FromResult(match);
        }
    }

    private sealed class StubTemplateRenderer : INotificationTemplateRenderer
    {
        public Dictionary<(string id, int version), (string body, string? subject)> Templates { get; } = new();

        public Task<NotificationMessage> RenderAsync(
            INotificationEvent notificationEvent,
            NotificationRoute route,
            IReadOnlyList<NotificationRecipient> recipients,
            CancellationToken cancellationToken = default)
        {
            if (!Templates.TryGetValue((route.Template.TemplateId, route.Template.Version), out var t))
            {
                throw new NotificationTemplateNotFoundException(route.Template);
            }

            // Simple {{ event_id }} substitution so tests can assert the binding flowed.
            var body = t.body.Replace("{{ event_id }}", notificationEvent.EventId.ToString(), StringComparison.Ordinal);

            return Task.FromResult(new NotificationMessage(
                EventId: notificationEvent.EventId,
                EventKind: notificationEvent.Kind,
                Channel: route.Recipients.Count > 0 ? route.Recipients[0].Channel : NotificationChannel.Unspecified,
                Recipients: recipients,
                Body: body,
                ActionUrl: notificationEvent.ActionUrl,
                Severity: notificationEvent.Severity,
                Subject: t.subject,
                Template: route.Template));
        }
    }

    private sealed class RecordingProvider(string id, NotificationChannel channel, bool throwOnSend = false)
        : INotificationProvider
    {
        public string Id { get; } = id;
        public NotificationChannel Channel { get; } = channel;
        public bool ThrowOnSend { get; } = throwOnSend;
        public List<NotificationMessage> SentMessages { get; } = new();
        public NotificationDeliveryResult? NextResult { get; set; }

        public Task<NotificationDeliveryResult> SendAsync(
            NotificationMessage message,
            NotificationRoute route,
            CancellationToken cancellationToken = default)
        {
            if (ThrowOnSend)
            {
                throw new InvalidOperationException("simulated provider transport failure");
            }

            SentMessages.Add(message);

            if (NextResult is { } stub)
            {
                return Task.FromResult(stub);
            }

            return Task.FromResult(new NotificationDeliveryResult(
                EventId: message.EventId,
                RouteId: route.RouteId,
                ProviderId: Id,
                Status: NotificationDeliveryStatus.Sent,
                AttemptedAtUtc: DateTimeOffset.UtcNow,
                CompletedAtUtc: DateTimeOffset.UtcNow.AddMilliseconds(20),
                AttemptNumber: 1,
                NormalizedDestination: message.Recipients.Count == 0 ? null : message.Recipients[0].Address,
                ProviderMessageId: $"msg-{Guid.NewGuid():N}"));
        }

        public Task<ProviderValidationResult> ValidateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(ProviderValidationResult.Valid());
    }
}
