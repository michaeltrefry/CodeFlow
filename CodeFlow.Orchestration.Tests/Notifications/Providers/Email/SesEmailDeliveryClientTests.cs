using Amazon.SimpleEmailV2.Model;
using CodeFlow.Orchestration.Notifications.Providers.Email;
using CodeFlow.Orchestration.Notifications.Providers.Email.Ses;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeFlow.Orchestration.Tests.Notifications.Providers.Email;

public sealed class SesEmailDeliveryClientTests
{
    [Fact]
    public async Task SendAsync_OnSuccess_ReturnsSentWithMessageId()
    {
        var ses = new RecordingSesClient
        {
            NextResponse = new SendEmailResponse { MessageId = "0000018a-1234-abcd" },
        };
        var client = new SesEmailDeliveryClient(ses, NullLogger<SesEmailDeliveryClient>.Instance);

        var outcome = await client.SendAsync(new EmailRequest(
            FromAddress: "ops@example.com",
            ToAddress: "reviewer@example.com",
            Subject: "HITL pending",
            TextBody: "body"));

        outcome.Success.Should().BeTrue();
        outcome.ProviderMessageId.Should().Be("0000018a-1234-abcd");

        ses.Captured.Should().ContainSingle();
        var sent = ses.Captured[0];
        sent.FromEmailAddress.Should().Be("ops@example.com");
        sent.Destination.ToAddresses.Should().ContainSingle().Which.Should().Be("reviewer@example.com");
        sent.Content.Simple.Subject.Data.Should().Be("HITL pending");
        sent.Content.Simple.Body.Text.Data.Should().Be("body");
        sent.Content.Simple.Subject.Charset.Should().Be("UTF-8");
        sent.Content.Simple.Body.Text.Charset.Should().Be("UTF-8");
    }

    [Fact]
    public async Task SendAsync_OnSesException_ReturnsFailedWithSnakeCasedNamespace()
    {
        var rejected = new MessageRejectedException("Email address is not verified.")
        {
            ErrorCode = "MessageRejected",
        };
        var ses = new RecordingSesClient { ThrowOnSend = rejected };
        var client = new SesEmailDeliveryClient(ses, NullLogger<SesEmailDeliveryClient>.Instance);

        var outcome = await client.SendAsync(new EmailRequest(
            FromAddress: "ops@example.com",
            ToAddress: "reviewer@example.com",
            Subject: "x",
            TextBody: "y"));

        outcome.Success.Should().BeFalse();
        outcome.ErrorCode.Should().Be("email.ses.message_rejected");
        outcome.ErrorMessage.Should().Be("Email address is not verified.");
    }

    [Fact]
    public async Task SendAsync_OnSesExceptionWithCompoundErrorCode_NormalisesToLowerSnakeCase()
    {
        var rejected = new MessageRejectedException("Mail-from domain not verified.")
        {
            ErrorCode = "MailFromDomainNotVerified",
        };
        var ses = new RecordingSesClient { ThrowOnSend = rejected };
        var client = new SesEmailDeliveryClient(ses, NullLogger<SesEmailDeliveryClient>.Instance);

        var outcome = await client.SendAsync(new EmailRequest(
            "ops@example.com", "reviewer@example.com", Subject: null, TextBody: "y"));

        outcome.Success.Should().BeFalse();
        outcome.ErrorCode.Should().Be("email.ses.mail_from_domain_not_verified");
    }

    [Fact]
    public async Task SendAsync_OnTransportException_ReturnsTransportErrorCode()
    {
        var ses = new RecordingSesClient { ThrowOnSend = new HttpRequestException("connection reset") };
        var client = new SesEmailDeliveryClient(ses, NullLogger<SesEmailDeliveryClient>.Instance);

        var outcome = await client.SendAsync(new EmailRequest(
            "ops@example.com", "reviewer@example.com", Subject: null, TextBody: "body"));

        outcome.Success.Should().BeFalse();
        outcome.ErrorCode.Should().Be("email.ses.transport_error");
        outcome.ErrorMessage.Should().Contain("connection reset");
    }

    [Fact]
    public async Task SendAsync_NullSubject_SendsEmptySubjectInsteadOfNull()
    {
        var ses = new RecordingSesClient { NextResponse = new SendEmailResponse { MessageId = "x" } };
        var client = new SesEmailDeliveryClient(ses, NullLogger<SesEmailDeliveryClient>.Instance);

        await client.SendAsync(new EmailRequest("ops@example.com", "reviewer@example.com", Subject: null, TextBody: "body"));

        ses.Captured[0].Content.Simple.Subject.Data.Should().Be(string.Empty);
    }

    private sealed class RecordingSesClient : ISesEmailClient
    {
        public List<SendEmailRequest> Captured { get; } = new();
        public SendEmailResponse? NextResponse { get; set; }
        public Exception? ThrowOnSend { get; set; }

        public Task<SendEmailResponse> SendEmailAsync(SendEmailRequest request, CancellationToken cancellationToken = default)
        {
            if (ThrowOnSend is not null)
            {
                throw ThrowOnSend;
            }
            Captured.Add(request);
            return Task.FromResult(NextResponse ?? new SendEmailResponse());
        }
    }
}
