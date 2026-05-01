using CodeFlow.Contracts.Notifications;
using CodeFlow.Orchestration.Notifications.Providers.Sms;
using CodeFlow.Orchestration.Notifications.Providers.Sms.Twilio;
using CodeFlow.Persistence.Notifications;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeFlow.Orchestration.Tests.Notifications.Providers.Sms;

public sealed class SmsNotificationProviderFactoryTests
{
    [Fact]
    public void Channel_IsSms()
    {
        var factory = new SmsNotificationProviderFactory(
            new StubHttpClientFactory(),
            NullLoggerFactory.Instance);

        factory.Channel.Should().Be(NotificationChannel.Sms);
    }

    [Fact]
    public async Task CreateAsync_BuildsProviderWithMatchingId()
    {
        var factory = new SmsNotificationProviderFactory(
            new StubHttpClientFactory(),
            NullLoggerFactory.Instance);

        var provider = await factory.CreateAsync(BuildConfig(
            "sms-twilio",
            credential: """{"account_sid":"ACtest","auth_token":"authsecret"}"""));

        provider.Should().NotBeNull();
        provider.Id.Should().Be("sms-twilio");
        provider.Channel.Should().Be(NotificationChannel.Sms);
    }

    [Fact]
    public async Task CreateAsync_NoCredential_Throws()
    {
        var factory = new SmsNotificationProviderFactory(
            new StubHttpClientFactory(),
            NullLoggerFactory.Instance);

        Func<Task> act = () => factory.CreateAsync(BuildConfig("sms-twilio", credential: null));

        await act.Should().ThrowAsync<TwilioSmsCredentialsException>()
            .WithMessage("*has no Twilio credential configured*");
    }

    [Fact]
    public async Task CreateAsync_MalformedCredential_Throws()
    {
        var factory = new SmsNotificationProviderFactory(
            new StubHttpClientFactory(),
            NullLoggerFactory.Instance);

        Func<Task> act = () => factory.CreateAsync(BuildConfig("sms-twilio", credential: "not-json"));

        await act.Should().ThrowAsync<TwilioSmsCredentialsException>()
            .WithMessage("*not valid JSON*");
    }

    private static NotificationProviderConfigWithCredential BuildConfig(string id, string? credential)
    {
        return new NotificationProviderConfigWithCredential(
            Config: new NotificationProviderConfig(
                Id: id,
                DisplayName: id,
                Channel: NotificationChannel.Sms,
                EndpointUrl: null,
                FromAddress: "+15551234567",
                HasCredential: credential is not null,
                AdditionalConfigJson: null,
                Enabled: true,
                IsArchived: false,
                CreatedAtUtc: DateTime.UtcNow,
                CreatedBy: null,
                UpdatedAtUtc: DateTime.UtcNow,
                UpdatedBy: null),
            PlaintextCredential: credential);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new NoOpHandler())
        {
            BaseAddress = SmsNotificationProviderFactory.TwilioDefaultBaseAddress,
        };
    }

    private sealed class NoOpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new NotImplementedException("StubHttpClientFactory's HttpClient is never sent through in factory tests.");
    }
}
