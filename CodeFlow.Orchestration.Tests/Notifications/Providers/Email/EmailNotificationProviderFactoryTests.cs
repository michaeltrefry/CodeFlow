using CodeFlow.Contracts.Notifications;
using CodeFlow.Orchestration.Notifications.Providers.Email;
using CodeFlow.Persistence.Notifications;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeFlow.Orchestration.Tests.Notifications.Providers.Email;

public sealed class EmailNotificationProviderFactoryTests
{
    [Fact]
    public void Channel_IsEmail()
    {
        var factory = new EmailNotificationProviderFactory(NullLoggerFactory.Instance);
        factory.Channel.Should().Be(NotificationChannel.Email);
    }

    [Fact]
    public async Task CreateAsync_SesEngine_BuildsAnEmailProviderInstance()
    {
        // Sanity check: dispatching on engine=ses produces a working provider whose Id mirrors
        // the config and Channel is Email. We don't reach the SDK because no send happens — the
        // factory just constructs the client + provider with the configured region.
        var factory = new EmailNotificationProviderFactory(NullLoggerFactory.Instance);
        var config = BuildConfig(
            id: "email-ses",
            from: "ops@example.com",
            additionalConfigJson: """{"engine":"ses","region":"us-east-1"}""",
            credential: """{"access_key":"AKIATEST","secret_key":"secret"}""");

        var provider = await factory.CreateAsync(config);

        provider.Should().NotBeNull();
        provider.Id.Should().Be("email-ses");
        provider.Channel.Should().Be(NotificationChannel.Email);
    }

    [Fact]
    public async Task CreateAsync_SmtpEngine_BuildsAnEmailProviderInstance()
    {
        var factory = new EmailNotificationProviderFactory(NullLoggerFactory.Instance);
        var config = BuildConfig(
            id: "email-smtp",
            from: "ops@example.com",
            additionalConfigJson: """{"engine":"smtp","host":"smtp.relay.example.com","port":587,"username":"app@example.com"}""",
            credential: "smtp-password");

        var provider = await factory.CreateAsync(config);

        provider.Should().NotBeNull();
        provider.Id.Should().Be("email-smtp");
        provider.Channel.Should().Be(NotificationChannel.Email);
    }

    [Fact]
    public async Task CreateAsync_SesEngineWithoutCredential_StillBuildsProvider_UsingDefaultAwsChain()
    {
        // No encrypted_credential set → the SDK uses its default credential chain (IAM role,
        // env vars, etc). The factory still happily constructs the provider; the credential is
        // resolved later when the SDK actually makes a request.
        var factory = new EmailNotificationProviderFactory(NullLoggerFactory.Instance);
        var config = BuildConfig(
            id: "email-ses-iam",
            from: "ops@example.com",
            additionalConfigJson: """{"engine":"ses","region":"us-east-1"}""",
            credential: null);

        var provider = await factory.CreateAsync(config);
        provider.Id.Should().Be("email-ses-iam");
    }

    [Fact]
    public async Task CreateAsync_MalformedAdditionalConfig_ThrowsEmailProviderSettingsException()
    {
        var factory = new EmailNotificationProviderFactory(NullLoggerFactory.Instance);
        var config = BuildConfig(
            id: "email-broken",
            from: "ops@example.com",
            additionalConfigJson: "definitely-not-json",
            credential: null);

        Func<Task> act = () => factory.CreateAsync(config);
        await act.Should().ThrowAsync<EmailProviderSettingsException>();
    }

    [Fact]
    public async Task CreateAsync_NonEmailChannel_ThrowsArgumentException()
    {
        var factory = new EmailNotificationProviderFactory(NullLoggerFactory.Instance);
        var config = new NotificationProviderConfigWithCredential(
            Config: new NotificationProviderConfig(
                Id: "wrong-channel",
                DisplayName: "wrong-channel",
                Channel: NotificationChannel.Slack,
                EndpointUrl: null,
                FromAddress: "ops@example.com",
                HasCredential: false,
                AdditionalConfigJson: """{"engine":"ses","region":"us-east-1"}""",
                Enabled: true,
                IsArchived: false,
                CreatedAtUtc: DateTime.UtcNow,
                CreatedBy: null,
                UpdatedAtUtc: DateTime.UtcNow,
                UpdatedBy: null),
            PlaintextCredential: null);

        Func<Task> act = () => factory.CreateAsync(config);
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*requires an Email channel config*");
    }

    private static NotificationProviderConfigWithCredential BuildConfig(
        string id,
        string? from,
        string additionalConfigJson,
        string? credential)
    {
        return new NotificationProviderConfigWithCredential(
            Config: new NotificationProviderConfig(
                Id: id,
                DisplayName: id,
                Channel: NotificationChannel.Email,
                EndpointUrl: null,
                FromAddress: from,
                HasCredential: credential is not null,
                AdditionalConfigJson: additionalConfigJson,
                Enabled: true,
                IsArchived: false,
                CreatedAtUtc: DateTime.UtcNow,
                CreatedBy: null,
                UpdatedAtUtc: DateTime.UtcNow,
                UpdatedBy: null),
            PlaintextCredential: credential);
    }
}
