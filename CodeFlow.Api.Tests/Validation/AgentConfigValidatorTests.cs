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

    [Fact]
    public void ValidateConfig_AcceptsDecisionOutputTemplates()
    {
        using var doc = JsonDocument.Parse("""
        {
            "provider": "openai",
            "model": "gpt-5",
            "decisionOutputTemplates": {
                "Approved": "shipped {{ decision }}",
                "Rejected": "blocked {{ decision }}",
                "*": "fallback {{ decision }}"
            }
        }
        """);

        var result = AgentConfigValidator.ValidateConfig(doc.RootElement);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void ValidateConfig_RejectsNonObjectDecisionOutputTemplates()
    {
        using var doc = JsonDocument.Parse("""{"provider":"openai","model":"gpt-5","decisionOutputTemplates":"nope"}""");

        var result = AgentConfigValidator.ValidateConfig(doc.RootElement);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("decisionOutputTemplates");
    }

    [Fact]
    public void ValidateConfig_RejectsInvalidPortName()
    {
        using var doc = JsonDocument.Parse("""{"provider":"openai","model":"gpt-5","decisionOutputTemplates":{"bad port":"tpl"}}""");

        var result = AgentConfigValidator.ValidateConfig(doc.RootElement);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("bad port");
    }

    [Fact]
    public void ValidateConfig_RejectsNonStringTemplateValue()
    {
        using var doc = JsonDocument.Parse("""{"provider":"openai","model":"gpt-5","decisionOutputTemplates":{"Approved":42}}""");

        var result = AgentConfigValidator.ValidateConfig(doc.RootElement);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("Approved");
    }

    [Fact]
    public void ValidateConfig_RejectsTooManyEntries()
    {
        var pairs = Enumerable.Range(0, 33)
            .Select(i => $"\"port{i}\":\"tpl\"")
            .ToArray();
        var json = "{\"provider\":\"openai\",\"model\":\"gpt-5\",\"decisionOutputTemplates\":{"
            + string.Join(",", pairs)
            + "}}";
        using var doc = JsonDocument.Parse(json);

        var result = AgentConfigValidator.ValidateConfig(doc.RootElement);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("at most");
    }

    [Fact]
    public void ValidateConfig_RejectsOversizeTemplate()
    {
        var oversize = new string('x', 16 * 1024 + 1);
        var json = "{\"provider\":\"openai\",\"model\":\"gpt-5\",\"decisionOutputTemplates\":{\"Approved\":\""
            + oversize
            + "\"}}";
        using var doc = JsonDocument.Parse(json);

        var result = AgentConfigValidator.ValidateConfig(doc.RootElement);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("Approved");
    }

    [Fact]
    public void ValidateConfig_AllowsWildcardOnly()
    {
        using var doc = JsonDocument.Parse("""{"provider":"openai","model":"gpt-5","decisionOutputTemplates":{"*":"fallback {{ decision }}"}}""");

        var result = AgentConfigValidator.ValidateConfig(doc.RootElement);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void ValidateConfig_AcceptsValidHistory()
    {
        using var doc = JsonDocument.Parse("""
        {
            "provider": "openai",
            "model": "gpt-5",
            "history": [
                {"role": "user", "content": "What's the format?"},
                {"role": "assistant", "content": "Always reply with a JSON envelope."}
            ]
        }
        """);

        var result = AgentConfigValidator.ValidateConfig(doc.RootElement);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void ValidateConfig_AcceptsNullHistory()
    {
        using var doc = JsonDocument.Parse("""{"provider":"openai","model":"gpt-5","history":null}""");

        var result = AgentConfigValidator.ValidateConfig(doc.RootElement);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void ValidateConfig_RejectsNonArrayHistory()
    {
        using var doc = JsonDocument.Parse("""{"provider":"openai","model":"gpt-5","history":"nope"}""");

        var result = AgentConfigValidator.ValidateConfig(doc.RootElement);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("history");
    }

    [Fact]
    public void ValidateConfig_RejectsHistoryEntryWithoutObject()
    {
        using var doc = JsonDocument.Parse("""{"provider":"openai","model":"gpt-5","history":["nope"]}""");

        var result = AgentConfigValidator.ValidateConfig(doc.RootElement);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("history[0]");
    }

    [Theory]
    [InlineData("system")]
    [InlineData("tool")]
    [InlineData("banana")]
    public void ValidateConfig_RejectsUnauthorableHistoryRole(string role)
    {
        var json = $$"""{"provider":"openai","model":"gpt-5","history":[{"role":"{{role}}","content":"x"}]}""";
        using var doc = JsonDocument.Parse(json);

        var result = AgentConfigValidator.ValidateConfig(doc.RootElement);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("history[0].role");
    }

    [Fact]
    public void ValidateConfig_RejectsHistoryWithEmptyContent()
    {
        using var doc = JsonDocument.Parse("""
        {"provider":"openai","model":"gpt-5","history":[{"role":"user","content":""}]}
        """);

        var result = AgentConfigValidator.ValidateConfig(doc.RootElement);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("content");
    }

    [Fact]
    public void ValidateConfig_RejectsHistoryWithMissingRole()
    {
        using var doc = JsonDocument.Parse("""
        {"provider":"openai","model":"gpt-5","history":[{"content":"x"}]}
        """);

        var result = AgentConfigValidator.ValidateConfig(doc.RootElement);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("role");
    }

    [Fact]
    public void ValidateConfig_RejectsTooManyHistoryEntries()
    {
        var entries = string.Join(",", Enumerable.Range(0, 33)
            .Select(_ => """{"role":"user","content":"x"}"""));
        var json = $$"""{"provider":"openai","model":"gpt-5","history":[{{entries}}]}""";
        using var doc = JsonDocument.Parse(json);

        var result = AgentConfigValidator.ValidateConfig(doc.RootElement);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("at most");
    }

    [Fact]
    public void ValidateConfig_RejectsOversizeHistoryTotal()
    {
        var oversize = new string('x', 32 * 1024 + 1);
        var json = $$"""{"provider":"openai","model":"gpt-5","history":[{"role":"user","content":"{{oversize}}"}]}""";
        using var doc = JsonDocument.Parse(json);

        var result = AgentConfigValidator.ValidateConfig(doc.RootElement);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("total content length");
    }
}
