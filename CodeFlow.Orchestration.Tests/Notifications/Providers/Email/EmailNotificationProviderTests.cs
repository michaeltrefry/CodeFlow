using CodeFlow.Contracts.Notifications;
using CodeFlow.Orchestration.Notifications.Providers.Email;
using CodeFlow.Persistence.Notifications;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeFlow.Orchestration.Tests.Notifications.Providers.Email;

public sealed class EmailNotificationProviderTests
{
    [Fact]
    public async Task SendAsync_OnDeliveryClientSuccess_ReturnsSentWithProviderMessageId()
    {
        var client = new RecordingDeliveryClient
        {
            NextOutcome = EmailDeliveryOutcome.Sent("ses-message-id-1234"),
        };
        var provider = BuildProvider("email-prod", "ops@example.com", client);

        var result = await provider.SendAsync(
            BuildMessage("reviewer@example.com", subject: "HITL pending", body: "Please review."),
            BuildRoute("email-prod"));

        result.Status.Should().Be(NotificationDeliveryStatus.Sent);
        result.ProviderMessageId.Should().Be("ses-message-id-1234");
        result.NormalizedDestination.Should().Be("reviewer@example.com");
        result.ErrorCode.Should().BeNull();

        client.Sent.Should().ContainSingle();
        client.Sent[0].FromAddress.Should().Be("ops@example.com");
        client.Sent[0].ToAddress.Should().Be("reviewer@example.com");
        client.Sent[0].Subject.Should().Be("HITL pending");
        client.Sent[0].TextBody.Should().Be("Please review.");
    }

    [Fact]
    public async Task SendAsync_OnDeliveryClientFailure_PropagatesEngineErrorCode()
    {
        var client = new RecordingDeliveryClient
        {
            NextOutcome = EmailDeliveryOutcome.Failed("email.ses.message_rejected", "From address not verified."),
        };
        var provider = BuildProvider("email-prod", "ops@example.com", client);

        var result = await provider.SendAsync(BuildMessage("reviewer@example.com"), BuildRoute("email-prod"));

        result.Status.Should().Be(NotificationDeliveryStatus.Failed);
        result.ErrorCode.Should().Be("email.ses.message_rejected");
        result.ErrorMessage.Should().Be("From address not verified.");
        result.ProviderMessageId.Should().BeNull();
    }

    [Fact]
    public async Task SendAsync_WhenRecipientsEmpty_ReturnsFailedWithoutCallingClient()
    {
        var client = new RecordingDeliveryClient();
        var provider = BuildProvider("email-prod", "ops@example.com", client);

        var emptyMessage = new NotificationMessage(
            EventId: Guid.NewGuid(),
            EventKind: NotificationEventKind.HitlTaskPending,
            Channel: NotificationChannel.Email,
            Recipients: Array.Empty<NotificationRecipient>(),
            Body: "no destination",
            ActionUrl: new Uri("https://codeflow.example.com/hitl?task=1"),
            Severity: NotificationSeverity.Normal);

        var result = await provider.SendAsync(emptyMessage, BuildRoute("email-prod"));

        result.Status.Should().Be(NotificationDeliveryStatus.Failed);
        result.ErrorCode.Should().Be("email.missing_recipient");
        client.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task SendAsync_WhenFromAddressMissing_ReturnsFailedWithoutCallingClient()
    {
        var client = new RecordingDeliveryClient();
        var provider = BuildProvider("email-prod", fromAddress: null, client);

        var result = await provider.SendAsync(BuildMessage("reviewer@example.com"), BuildRoute("email-prod"));

        result.Status.Should().Be(NotificationDeliveryStatus.Failed);
        result.ErrorCode.Should().Be("email.missing_from_address");
        client.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task SendAsync_WhenDeliveryClientThrowsUnexpectedly_RecordsClientExceptionFailure()
    {
        var client = new RecordingDeliveryClient { ThrowOnSend = new InvalidOperationException("ouch") };
        var provider = BuildProvider("email-prod", "ops@example.com", client);

        var result = await provider.SendAsync(BuildMessage("reviewer@example.com"), BuildRoute("email-prod"));

        result.Status.Should().Be(NotificationDeliveryStatus.Failed);
        result.ErrorCode.Should().Be("email.client_exception");
        result.ErrorMessage.Should().Be("ouch");
    }

    [Fact]
    public async Task ValidateAsync_WhenFromAddressMissing_ReturnsInvalid()
    {
        var provider = BuildProvider("email-prod", fromAddress: null, new RecordingDeliveryClient());
        var result = await provider.ValidateAsync();

        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be("email.missing_from_address");
    }

    [Fact]
    public async Task ValidateAsync_WhenClientImplementsValidator_DelegatesToIt()
    {
        var client = new ValidatingDeliveryClient
        {
            ValidationResult = ProviderValidationResult.Invalid("email.ses.message_rejected", "from-address not verified"),
        };
        var provider = BuildProvider("email-prod", "ops@example.com", client);

        var result = await provider.ValidateAsync();

        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be("email.ses.message_rejected");
        client.ValidateCount.Should().Be(1);
    }

    [Fact]
    public async Task ValidateAsync_WhenClientHasNoValidator_ReturnsValid()
    {
        var provider = BuildProvider("email-prod", "ops@example.com", new RecordingDeliveryClient());
        var result = await provider.ValidateAsync();
        result.IsValid.Should().BeTrue();
    }

    private static EmailNotificationProvider BuildProvider(
        string id,
        string? fromAddress,
        IEmailDeliveryClient client)
    {
        var config = new NotificationProviderConfigWithCredential(
            Config: new NotificationProviderConfig(
                Id: id,
                DisplayName: $"Email {id}",
                Channel: NotificationChannel.Email,
                EndpointUrl: null,
                FromAddress: fromAddress,
                HasCredential: false,
                AdditionalConfigJson: """{"engine":"ses","region":"us-east-1"}""",
                Enabled: true,
                IsArchived: false,
                CreatedAtUtc: DateTime.UtcNow,
                CreatedBy: null,
                UpdatedAtUtc: DateTime.UtcNow,
                UpdatedBy: null),
            PlaintextCredential: null);

        return new EmailNotificationProvider(config, client, NullLogger<EmailNotificationProvider>.Instance);
    }

    private static NotificationRoute BuildRoute(string providerId) => new(
        RouteId: $"route-{providerId}",
        EventKind: NotificationEventKind.HitlTaskPending,
        ProviderId: providerId,
        Recipients: [new NotificationRecipient(NotificationChannel.Email, "reviewer@example.com")],
        Template: new NotificationTemplateRef("hitl-task-pending/email/default", 1));

    private static NotificationMessage BuildMessage(
        string toAddress,
        string? subject = "HITL pending",
        string body = "Please review.")
    {
        return new NotificationMessage(
            EventId: Guid.NewGuid(),
            EventKind: NotificationEventKind.HitlTaskPending,
            Channel: NotificationChannel.Email,
            Recipients: [new NotificationRecipient(NotificationChannel.Email, toAddress)],
            Body: body,
            ActionUrl: new Uri("https://codeflow.example.com/hitl?task=42"),
            Severity: NotificationSeverity.Normal,
            Subject: subject,
            Template: new NotificationTemplateRef("hitl-task-pending/email/default", 1));
    }

    private sealed class RecordingDeliveryClient : IEmailDeliveryClient
    {
        public List<EmailRequest> Sent { get; } = new();
        public EmailDeliveryOutcome NextOutcome { get; set; } = EmailDeliveryOutcome.Sent("default-message-id");
        public Exception? ThrowOnSend { get; set; }

        public Task<EmailDeliveryOutcome> SendAsync(EmailRequest request, CancellationToken cancellationToken = default)
        {
            if (ThrowOnSend is not null)
            {
                throw ThrowOnSend;
            }
            Sent.Add(request);
            return Task.FromResult(NextOutcome);
        }
    }

    private sealed class ValidatingDeliveryClient : IEmailDeliveryClient, IEmailDeliveryClientValidator
    {
        public ProviderValidationResult ValidationResult { get; set; } = ProviderValidationResult.Valid();
        public int ValidateCount { get; private set; }

        public Task<EmailDeliveryOutcome> SendAsync(EmailRequest request, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<ProviderValidationResult> ValidateAsync(CancellationToken cancellationToken = default)
        {
            ValidateCount++;
            return Task.FromResult(ValidationResult);
        }
    }
}
