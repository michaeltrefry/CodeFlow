using CodeFlow.Orchestration.Notifications.Providers.Sms.Twilio;
using FluentAssertions;

namespace CodeFlow.Orchestration.Tests.Notifications.Providers.Sms;

public sealed class TwilioSmsCredentialsTests
{
    [Fact]
    public void Parse_NullOrEmpty_ReturnsNull()
    {
        TwilioSmsCredentials.Parse(null).Should().BeNull();
        TwilioSmsCredentials.Parse("").Should().BeNull();
        TwilioSmsCredentials.Parse("   ").Should().BeNull();
    }

    [Fact]
    public void Parse_ValidJson_PopulatesAccountSidAndAuthToken()
    {
        var creds = TwilioSmsCredentials.Parse("""{"account_sid":"AC1234","auth_token":"verysecret"}""");
        creds.Should().NotBeNull();
        creds!.AccountSid.Should().Be("AC1234");
        creds.AuthToken.Should().Be("verysecret");
    }

    [Fact]
    public void Parse_MissingAccountSid_Throws()
    {
        Action act = () => TwilioSmsCredentials.Parse("""{"auth_token":"only"}""");
        act.Should().Throw<TwilioSmsCredentialsException>()
            .WithMessage("*missing account_sid or auth_token*");
    }

    [Fact]
    public void Parse_MissingAuthToken_Throws()
    {
        Action act = () => TwilioSmsCredentials.Parse("""{"account_sid":"AC1234"}""");
        act.Should().Throw<TwilioSmsCredentialsException>()
            .WithMessage("*missing account_sid or auth_token*");
    }

    [Fact]
    public void Parse_MalformedJson_Throws()
    {
        Action act = () => TwilioSmsCredentials.Parse("not-json");
        act.Should().Throw<TwilioSmsCredentialsException>()
            .WithMessage("*not valid JSON*");
    }
}
