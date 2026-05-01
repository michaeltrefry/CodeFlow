using CodeFlow.Contracts.Notifications;
using CodeFlow.Orchestration.Notifications;
using CodeFlow.Persistence.Notifications;
using CodeFlow.Runtime;
using FluentAssertions;

namespace CodeFlow.Orchestration.Tests.Notifications;

public sealed class ScribanNotificationTemplateRendererTests
{
    [Fact]
    public async Task RenderAsync_BindsEventFieldsAsSnakeCase_AndPreservesActionUrl()
    {
        var templates = new InMemoryTemplateRepository();
        templates.AddTemplate(
            "hitl-task-pending/email/default",
            1,
            NotificationEventKind.HitlTaskPending,
            NotificationChannel.Email,
            subject: "[CodeFlow] HITL review needed: {{ workflow_key }} v{{ workflow_version }}",
            body: """
                A HITL task is pending.
                Trace: {{ trace_id }}
                Agent: {{ agent_key }} v{{ agent_version }}
                Open: {{ action_url }}
                """);

        var renderer = new ScribanNotificationTemplateRenderer(templates, new ScribanTemplateRenderer());

        var traceId = Guid.NewGuid();
        var evt = new HitlTaskPendingEvent(
            EventId: Guid.NewGuid(),
            OccurredAtUtc: DateTimeOffset.UtcNow,
            ActionUrl: new Uri("https://codeflow.example.com/hitl/4242"),
            Severity: NotificationSeverity.High,
            HitlTaskId: 4242,
            TraceId: traceId,
            RoundId: Guid.NewGuid(),
            NodeId: Guid.NewGuid(),
            WorkflowKey: "review-loop-v3",
            WorkflowVersion: 7,
            AgentKey: "reviewer",
            AgentVersion: 2,
            HitlTaskCreatedAtUtc: DateTimeOffset.UtcNow);

        var route = new NotificationRoute(
            RouteId: "route-email",
            EventKind: NotificationEventKind.HitlTaskPending,
            ProviderId: "email-mailgun",
            Recipients: [new NotificationRecipient(NotificationChannel.Email, "ops@example.com")],
            Template: new NotificationTemplateRef("hitl-task-pending/email/default", 1));

        var message = await renderer.RenderAsync(evt, route, route.Recipients);

        message.Subject.Should().Be("[CodeFlow] HITL review needed: review-loop-v3 v7");
        message.Body.Should().Contain($"Trace: {traceId}");
        message.Body.Should().Contain("Agent: reviewer v2");
        message.Body.Should().Contain("Open: https://codeflow.example.com/hitl/4242");
        message.ActionUrl.Should().Be(evt.ActionUrl);
        message.Channel.Should().Be(NotificationChannel.Email);
        message.Template.Should().Be(route.Template);
    }

    [Fact]
    public async Task RenderAsync_TemplateMissing_ThrowsNotificationTemplateNotFoundException()
    {
        var renderer = new ScribanNotificationTemplateRenderer(new InMemoryTemplateRepository(), new ScribanTemplateRenderer());

        var route = new NotificationRoute(
            RouteId: "route-email",
            EventKind: NotificationEventKind.HitlTaskPending,
            ProviderId: "email-mailgun",
            Recipients: [new NotificationRecipient(NotificationChannel.Email, "ops@example.com")],
            Template: new NotificationTemplateRef("missing-template", 99));

        var evt = new HitlTaskPendingEvent(
            EventId: Guid.NewGuid(),
            OccurredAtUtc: DateTimeOffset.UtcNow,
            ActionUrl: new Uri("https://codeflow.example.com/hitl/1"),
            Severity: NotificationSeverity.Normal,
            HitlTaskId: 1,
            TraceId: Guid.NewGuid(),
            RoundId: Guid.NewGuid(),
            NodeId: Guid.NewGuid(),
            WorkflowKey: "wf",
            WorkflowVersion: 1,
            AgentKey: "agent",
            AgentVersion: 1,
            HitlTaskCreatedAtUtc: DateTimeOffset.UtcNow);

        Func<Task> act = () => renderer.RenderAsync(evt, route, route.Recipients);
        var exception = await act.Should().ThrowAsync<NotificationTemplateNotFoundException>();
        exception.Which.TemplateRef.TemplateId.Should().Be("missing-template");
        exception.Which.TemplateRef.Version.Should().Be(99);
    }

    [Fact]
    public async Task RenderAsync_NullSubjectTemplate_ProducesNullSubject()
    {
        var templates = new InMemoryTemplateRepository();
        templates.AddTemplate(
            "tmpl/sms",
            1,
            NotificationEventKind.HitlTaskPending,
            NotificationChannel.Sms,
            subject: null,
            body: "HITL: {{ action_url }}");

        var renderer = new ScribanNotificationTemplateRenderer(templates, new ScribanTemplateRenderer());

        var evt = new HitlTaskPendingEvent(
            EventId: Guid.NewGuid(),
            OccurredAtUtc: DateTimeOffset.UtcNow,
            ActionUrl: new Uri("https://codeflow.example.com/hitl/1"),
            Severity: NotificationSeverity.Normal,
            HitlTaskId: 1,
            TraceId: Guid.NewGuid(),
            RoundId: Guid.NewGuid(),
            NodeId: Guid.NewGuid(),
            WorkflowKey: "wf",
            WorkflowVersion: 1,
            AgentKey: "agent",
            AgentVersion: 1,
            HitlTaskCreatedAtUtc: DateTimeOffset.UtcNow);

        var route = new NotificationRoute(
            RouteId: "route-sms",
            EventKind: NotificationEventKind.HitlTaskPending,
            ProviderId: "sms-twilio",
            Recipients: [new NotificationRecipient(NotificationChannel.Sms, "+15550001111")],
            Template: new NotificationTemplateRef("tmpl/sms", 1));

        var message = await renderer.RenderAsync(evt, route, route.Recipients);

        message.Subject.Should().BeNull();
        message.Body.Should().Be("HITL: https://codeflow.example.com/hitl/1");
    }

    private sealed class InMemoryTemplateRepository : INotificationTemplateRepository
    {
        private readonly Dictionary<(string id, int version), NotificationTemplate> templates = new();

        public void AddTemplate(
            string id,
            int version,
            NotificationEventKind eventKind,
            NotificationChannel channel,
            string? subject,
            string body)
        {
            var now = DateTime.UtcNow;
            templates[(id, version)] = new NotificationTemplate(
                TemplateId: id,
                Version: version,
                EventKind: eventKind,
                Channel: channel,
                SubjectTemplate: subject,
                BodyTemplate: body,
                CreatedAtUtc: now,
                CreatedBy: null,
                UpdatedAtUtc: now,
                UpdatedBy: null);
        }

        public Task<IReadOnlyList<NotificationTemplate>> ListVersionsAsync(string templateId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<NotificationTemplate>>(
                templates.Values.Where(t => t.TemplateId == templateId).OrderByDescending(t => t.Version).ToArray());

        public Task<IReadOnlyList<NotificationTemplate>> ListLatestPerTemplateAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<NotificationTemplate>>(
                templates.Values
                    .GroupBy(t => t.TemplateId)
                    .Select(g => g.OrderByDescending(t => t.Version).First())
                    .OrderBy(t => t.TemplateId)
                    .ToArray());

        public Task<NotificationTemplate?> GetAsync(string templateId, int version, CancellationToken ct = default) =>
            Task.FromResult(templates.TryGetValue((templateId, version), out var t) ? t : null);

        public Task<NotificationTemplate?> GetLatestAsync(string templateId, CancellationToken ct = default)
        {
            var latest = templates.Values
                .Where(t => t.TemplateId == templateId)
                .OrderByDescending(t => t.Version)
                .FirstOrDefault();
            return Task.FromResult(latest);
        }

        public Task<NotificationTemplate> PublishAsync(NotificationTemplateUpsert upsert, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
