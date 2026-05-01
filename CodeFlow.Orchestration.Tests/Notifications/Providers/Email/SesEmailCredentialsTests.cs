using CodeFlow.Orchestration.Notifications.Providers.Email;
using CodeFlow.Orchestration.Notifications.Providers.Email.Ses;
using FluentAssertions;

namespace CodeFlow.Orchestration.Tests.Notifications.Providers.Email;

public sealed class SesEmailCredentialsTests
{
    [Fact]
    public void Parse_NullOrEmpty_ReturnsNull_ForDefaultAwsCredentialChain()
    {
        SesEmailCredentials.Parse(null).Should().BeNull();
        SesEmailCredentials.Parse("").Should().BeNull();
        SesEmailCredentials.Parse("   ").Should().BeNull();
    }

    [Fact]
    public void Parse_ValidJson_PopulatesAccessAndSecretKeys()
    {
        var creds = SesEmailCredentials.Parse("""{"access_key":"AKIATEST","secret_key":"verysecret"}""");
        creds.Should().NotBeNull();
        creds!.AccessKey.Should().Be("AKIATEST");
        creds.SecretKey.Should().Be("verysecret");
    }

    [Fact]
    public void Parse_MissingAccessKey_Throws()
    {
        Action act = () => SesEmailCredentials.Parse("""{"secret_key":"only"}""");
        act.Should().Throw<EmailProviderSettingsException>()
            .WithMessage("*missing access_key or secret_key*");
    }

    [Fact]
    public void Parse_MalformedJson_Throws()
    {
        Action act = () => SesEmailCredentials.Parse("not-json");
        act.Should().Throw<EmailProviderSettingsException>()
            .WithMessage("*not valid JSON*");
    }
}
