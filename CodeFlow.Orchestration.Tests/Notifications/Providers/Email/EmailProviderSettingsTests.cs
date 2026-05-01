using CodeFlow.Orchestration.Notifications.Providers.Email;
using FluentAssertions;

namespace CodeFlow.Orchestration.Tests.Notifications.Providers.Email;

public sealed class EmailProviderSettingsTests
{
    [Fact]
    public void Parse_SesEngine_PopulatesRegion()
    {
        var settings = EmailProviderSettings.Parse("""{ "engine": "ses", "region": "us-east-1" }""");

        settings.Engine.Should().Be(EmailEngine.Ses);
        settings.Ses!.Region.Should().Be("us-east-1");
        settings.Smtp.Should().BeNull();
    }

    [Fact]
    public void Parse_SmtpEngine_PopulatesAllFieldsAndDefaultsStartTlsTrue()
    {
        var settings = EmailProviderSettings.Parse("""
            {
              "engine": "smtp",
              "host": "smtp.relay.example.com",
              "port": 2525,
              "username": "app@example.com"
            }
            """);

        settings.Engine.Should().Be(EmailEngine.Smtp);
        settings.Smtp!.Host.Should().Be("smtp.relay.example.com");
        settings.Smtp.Port.Should().Be(2525);
        settings.Smtp.Username.Should().Be("app@example.com");
        settings.Smtp.UseStartTls.Should().BeTrue();
        settings.Ses.Should().BeNull();
    }

    [Fact]
    public void Parse_SmtpEngine_RespectsExplicitUseStartTlsFalse()
    {
        var settings = EmailProviderSettings.Parse("""
            { "engine": "smtp", "host": "localhost", "port": 25, "use_start_tls": false }
            """);

        settings.Smtp!.UseStartTls.Should().BeFalse();
    }

    [Fact]
    public void Parse_SmtpEngine_DefaultsPortTo587WhenOmitted()
    {
        var settings = EmailProviderSettings.Parse("""{ "engine": "smtp", "host": "smtp.relay.example.com" }""");
        settings.Smtp!.Port.Should().Be(587);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Parse_EmptyJson_ThrowsWithHelpfulMessage(string? input)
    {
        Action act = () => EmailProviderSettings.Parse(input);
        act.Should().Throw<EmailProviderSettingsException>()
            .WithMessage("*additional_config_json is empty*");
    }

    [Fact]
    public void Parse_UnknownEngine_Throws()
    {
        Action act = () => EmailProviderSettings.Parse("""{ "engine": "carrier-pigeon" }""");
        act.Should().Throw<EmailProviderSettingsException>()
            .WithMessage("*Unknown email engine 'carrier-pigeon'*");
    }

    [Fact]
    public void Parse_SesMissingRegion_Throws()
    {
        Action act = () => EmailProviderSettings.Parse("""{ "engine": "ses" }""");
        act.Should().Throw<EmailProviderSettingsException>()
            .WithMessage("*SES engine requires 'region'*");
    }

    [Fact]
    public void Parse_SmtpMissingHost_Throws()
    {
        Action act = () => EmailProviderSettings.Parse("""{ "engine": "smtp", "port": 25 }""");
        act.Should().Throw<EmailProviderSettingsException>()
            .WithMessage("*SMTP engine requires 'host'*");
    }

    [Fact]
    public void Parse_SmtpInvalidPort_Throws()
    {
        Action act = () => EmailProviderSettings.Parse("""{ "engine": "smtp", "host": "x", "port": 99999 }""");
        act.Should().Throw<EmailProviderSettingsException>()
            .WithMessage("*'port' must be 1..65535*");
    }

    [Fact]
    public void Parse_MalformedJson_Throws()
    {
        Action act = () => EmailProviderSettings.Parse("not-json");
        act.Should().Throw<EmailProviderSettingsException>()
            .WithMessage("*not valid JSON*");
    }
}
