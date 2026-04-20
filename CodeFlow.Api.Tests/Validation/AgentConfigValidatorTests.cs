using CodeFlow.Api.Validation;
using FluentAssertions;
using System.Text.Json;

namespace CodeFlow.Api.Tests.Validation;

public sealed class AgentConfigValidatorTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Reviewer")]
    [InlineData("has space")]
    [InlineData("bad!char")]
    public void ValidateKey_RejectsInvalid(string input)
    {
        AgentConfigValidator.ValidateKey(input).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("reviewer")]
    [InlineData("article-flow")]
    [InlineData("echo_agent")]
    [InlineData("a1")]
    public void ValidateKey_AcceptsValid(string input)
    {
        AgentConfigValidator.ValidateKey(input).IsValid.Should().BeTrue();
    }

    [Fact]
    public void ValidateConfig_RejectsMissing()
    {
        AgentConfigValidator.ValidateConfig(null).IsValid.Should().BeFalse();
    }

    [Fact]
    public void ValidateConfig_RequiresProviderAndModelForAgent()
    {
        using var doc = JsonDocument.Parse("""{"type":"agent"}""");
        var result = AgentConfigValidator.ValidateConfig(doc.RootElement);
        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("provider");
    }

    [Fact]
    public void ValidateConfig_HitlDoesNotRequireModelOrProvider()
    {
        using var doc = JsonDocument.Parse("""{"type":"hitl"}""");
        var result = AgentConfigValidator.ValidateConfig(doc.RootElement);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void ValidateConfig_RejectsUnknownProvider()
    {
        using var doc = JsonDocument.Parse("""{"provider":"banana","model":"x"}""");
        var result = AgentConfigValidator.ValidateConfig(doc.RootElement);
        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("Unknown provider");
    }

    [Fact]
    public void ValidateConfig_AcceptsValidAgent()
    {
        using var doc = JsonDocument.Parse("""{"provider":"openai","model":"gpt-5"}""");
        var result = AgentConfigValidator.ValidateConfig(doc.RootElement);
        result.IsValid.Should().BeTrue();
    }
}
