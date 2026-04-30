using System.Text.Json;
using CodeFlow.Contracts.Notifications;
using FluentAssertions;

namespace CodeFlow.Contracts.Tests;

public sealed class NotificationContractTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void HitlTaskPendingEvent_ShouldRoundTripThroughJsonSerialization()
    {
        var evt = new HitlTaskPendingEvent(
            EventId: Guid.NewGuid(),
            OccurredAtUtc: DateTimeOffset.UtcNow,
            ActionUrl: new Uri("https://codeflow.example.com/hitl/4242"),
            Severity: NotificationSeverity.High,
            HitlTaskId: 4242,
            TraceId: Guid.NewGuid(),
            RoundId: Guid.NewGuid(),
            NodeId: Guid.NewGuid(),
            WorkflowKey: "review-loop-v3",
            WorkflowVersion: 7,
            AgentKey: "reviewer",
            AgentVersion: 2,
            HitlTaskCreatedAtUtc: DateTimeOffset.UtcNow,
            InputPreview: "PRD draft v2 (1,842 chars)…",
            InputRef: new Uri("file:///tmp/codeflow/trace/hitl-input.bin"),
            SubflowPath: "root/review/qa");

        var json = JsonSerializer.Serialize(evt, SerializerOptions);
        var roundTripped = JsonSerializer.Deserialize<HitlTaskPendingEvent>(json, SerializerOptions);

        roundTripped.Should().NotBeNull();
        roundTripped.Should().BeEquivalentTo(evt);
        roundTripped!.Kind.Should().Be(NotificationEventKind.HitlTaskPending);
    }

    [Fact]
    public void HitlTaskPendingEvent_ShouldOmitOptionalFieldsWhenAbsent()
    {
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

        var json = JsonSerializer.Serialize(evt, SerializerOptions);
        var roundTripped = JsonSerializer.Deserialize<HitlTaskPendingEvent>(json, SerializerOptions);

        roundTripped.Should().NotBeNull();
        roundTripped!.InputPreview.Should().BeNull();
        roundTripped.InputRef.Should().BeNull();
        roundTripped.SubflowPath.Should().BeNull();
    }

    [Fact]
    public void HitlTaskPendingEvent_ShouldExposeMarkerInterface()
    {
        INotificationEvent evt = new HitlTaskPendingEvent(
            EventId: Guid.NewGuid(),
            OccurredAtUtc: DateTimeOffset.UtcNow,
            ActionUrl: new Uri("https://codeflow.example.com/hitl/1"),
            Severity: NotificationSeverity.Urgent,
            HitlTaskId: 1,
            TraceId: Guid.NewGuid(),
            RoundId: Guid.NewGuid(),
            NodeId: Guid.NewGuid(),
            WorkflowKey: "wf",
            WorkflowVersion: 1,
            AgentKey: "agent",
            AgentVersion: 1,
            HitlTaskCreatedAtUtc: DateTimeOffset.UtcNow);

        evt.Kind.Should().Be(NotificationEventKind.HitlTaskPending);
        evt.Severity.Should().Be(NotificationSeverity.Urgent);
        evt.ActionUrl.Should().NotBeNull();
        evt.EventId.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public void NotificationMessage_ShouldRoundTripThroughJsonSerialization()
    {
        var message = new NotificationMessage(
            EventId: Guid.NewGuid(),
            EventKind: NotificationEventKind.HitlTaskPending,
            Channel: NotificationChannel.Email,
            Recipients:
            [
                new NotificationRecipient(NotificationChannel.Email, "reviewer@example.com", "On-call Reviewer"),
                new NotificationRecipient(NotificationChannel.Email, "backup@example.com")
            ],
            Body: "A HITL task is pending. Open it: https://codeflow.example.com/hitl/4242",
            ActionUrl: new Uri("https://codeflow.example.com/hitl/4242"),
            Severity: NotificationSeverity.High,
            Subject: "[CodeFlow] HITL review needed: review-loop-v3",
            Template: new NotificationTemplateRef("hitl-task-pending/email/default", 3));

        var json = JsonSerializer.Serialize(message, SerializerOptions);
        var roundTripped = JsonSerializer.Deserialize<NotificationMessage>(json, SerializerOptions);

        roundTripped.Should().NotBeNull();
        roundTripped.Should().BeEquivalentTo(message);
        roundTripped!.Recipients.Should().HaveCount(2);
        roundTripped.Template!.TemplateId.Should().Be("hitl-task-pending/email/default");
        roundTripped.Template.Version.Should().Be(3);
    }

    [Fact]
    public void NotificationMessage_ShouldAllowNullSubjectForSmsShapedChannels()
    {
        var message = new NotificationMessage(
            EventId: Guid.NewGuid(),
            EventKind: NotificationEventKind.HitlTaskPending,
            Channel: NotificationChannel.Sms,
            Recipients: [new NotificationRecipient(NotificationChannel.Sms, "+15551234567")],
            Body: "CodeFlow HITL: open https://codeflow.example.com/hitl/1",
            ActionUrl: new Uri("https://codeflow.example.com/hitl/1"),
            Severity: NotificationSeverity.Normal);

        var json = JsonSerializer.Serialize(message, SerializerOptions);
        var roundTripped = JsonSerializer.Deserialize<NotificationMessage>(json, SerializerOptions);

        roundTripped.Should().NotBeNull();
        roundTripped!.Subject.Should().BeNull();
        roundTripped.Template.Should().BeNull();
    }

    [Fact]
    public void NotificationRoute_ShouldRoundTripWithDefaults()
    {
        var route = new NotificationRoute(
            RouteId: "route-hitl-pending-slack",
            EventKind: NotificationEventKind.HitlTaskPending,
            ProviderId: "slack-prod",
            Recipients: [new NotificationRecipient(NotificationChannel.Slack, "C012AB3CD", "#hitl-queue")],
            Template: new NotificationTemplateRef("hitl-task-pending/slack/default", 1));

        var json = JsonSerializer.Serialize(route, SerializerOptions);
        var roundTripped = JsonSerializer.Deserialize<NotificationRoute>(json, SerializerOptions);

        roundTripped.Should().NotBeNull();
        roundTripped!.MinimumSeverity.Should().Be(NotificationSeverity.Info);
        roundTripped.Enabled.Should().BeTrue();
        roundTripped.Recipients.Should().ContainSingle().Which.Channel.Should().Be(NotificationChannel.Slack);
    }

    [Fact]
    public void NotificationDeliveryResult_ShouldRoundTripSuccessfulSend()
    {
        var result = new NotificationDeliveryResult(
            EventId: Guid.NewGuid(),
            RouteId: "route-hitl-pending-email",
            ProviderId: "email-mailgun",
            Status: NotificationDeliveryStatus.Sent,
            AttemptedAtUtc: DateTimeOffset.UtcNow,
            CompletedAtUtc: DateTimeOffset.UtcNow.AddMilliseconds(120),
            AttemptNumber: 1,
            NormalizedDestination: "r***@example.com",
            ProviderMessageId: "<20260430.0001@mailgun>");

        var json = JsonSerializer.Serialize(result, SerializerOptions);
        var roundTripped = JsonSerializer.Deserialize<NotificationDeliveryResult>(json, SerializerOptions);

        roundTripped.Should().NotBeNull();
        roundTripped.Should().BeEquivalentTo(result);
    }

    [Fact]
    public void NotificationDeliveryResult_ShouldRoundTripFailureWithErrorDetail()
    {
        var result = new NotificationDeliveryResult(
            EventId: Guid.NewGuid(),
            RouteId: "route-hitl-pending-sms",
            ProviderId: "sms-twilio",
            Status: NotificationDeliveryStatus.Failed,
            AttemptedAtUtc: DateTimeOffset.UtcNow,
            CompletedAtUtc: DateTimeOffset.UtcNow.AddMilliseconds(40),
            AttemptNumber: 2,
            NormalizedDestination: "+1555*****67",
            ErrorCode: "twilio.21610",
            ErrorMessage: "Recipient is unsubscribed.");

        var json = JsonSerializer.Serialize(result, SerializerOptions);
        var roundTripped = JsonSerializer.Deserialize<NotificationDeliveryResult>(json, SerializerOptions);

        roundTripped.Should().NotBeNull();
        roundTripped!.Status.Should().Be(NotificationDeliveryStatus.Failed);
        roundTripped.ErrorCode.Should().Be("twilio.21610");
        roundTripped.ProviderMessageId.Should().BeNull();
    }

    [Fact]
    public void ProviderValidationResult_ShouldExposeFactoryHelpers()
    {
        var ok = ProviderValidationResult.Valid();
        ok.IsValid.Should().BeTrue();
        ok.ErrorCode.Should().BeNull();
        ok.ErrorMessage.Should().BeNull();

        var bad = ProviderValidationResult.Invalid("slack.auth_failed", "invalid_auth");
        bad.IsValid.Should().BeFalse();
        bad.ErrorCode.Should().Be("slack.auth_failed");
        bad.ErrorMessage.Should().Be("invalid_auth");
    }
}
