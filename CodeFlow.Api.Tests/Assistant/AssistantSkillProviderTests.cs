using CodeFlow.Api.Assistant.Skills;
using FluentAssertions;

namespace CodeFlow.Api.Tests.Assistant;

/// <summary>
/// Unit tests for the embedded assistant-skill provider and the markdown frontmatter parser
/// behind it. Drives the test-only constructor that takes pre-materialized
/// <c>(fileName, content)</c> pairs so the cases don't need to round-trip through the
/// build's embedded-resource pipeline.
/// </summary>
public sealed class AssistantSkillProviderTests
{
    [Fact]
    public void EmptySources_ProducesEmptyCatalog()
    {
        var provider = new EmbeddedAssistantSkillProvider(Array.Empty<(string, string)>());

        provider.List().Should().BeEmpty();
        provider.Get("anything").Should().BeNull();
    }

    [Fact]
    public void Parse_ExtractsKeyNameDescriptionAndBody()
    {
        var content = """
            ---
            key: workflow-authoring
            name: Workflow authoring
            description: Use when drafting or saving workflow JSON.
            ---

            Body line 1.

            Body line 2.
            """;

        var skill = AssistantSkillParser.Parse("test.md", content);

        skill.Key.Should().Be("workflow-authoring");
        skill.Name.Should().Be("Workflow authoring");
        skill.Description.Should().Be("Use when drafting or saving workflow JSON.");
        skill.Body.Should().Be("Body line 1.\n\nBody line 2.");
    }

    [Fact]
    public void Parse_NormalizesCrlfLineEndings()
    {
        var content = "---\r\nkey: alpha\r\nname: Alpha\r\ndescription: Alpha desc\r\n---\r\n\r\nBody.\r\n";

        var skill = AssistantSkillParser.Parse("alpha.md", content);

        skill.Body.Should().Be("Body.");
    }

    [Fact]
    public void Parse_ToleratesLeadingBlankLines()
    {
        var content = "\n\n---\nkey: alpha\nname: Alpha\ndescription: Alpha desc\n---\nBody.";

        var skill = AssistantSkillParser.Parse("alpha.md", content);

        skill.Key.Should().Be("alpha");
        skill.Body.Should().Be("Body.");
    }

    [Fact]
    public void Parse_RejectsMissingFrontmatterDelimiter()
    {
        var content = "key: alpha\nname: Alpha\ndescription: Alpha desc\n\nBody.";

        var act = () => AssistantSkillParser.Parse("alpha.md", content);

        act.Should().Throw<InvalidSkillSourceException>()
            .Which.Message.Should().Contain("frontmatter delimiter");
    }

    [Fact]
    public void Parse_RejectsUnclosedFrontmatter()
    {
        var content = "---\nkey: alpha\nname: Alpha\ndescription: Alpha desc\n\nBody (but no closing ---)";

        var act = () => AssistantSkillParser.Parse("alpha.md", content);

        act.Should().Throw<InvalidSkillSourceException>()
            .Which.Message.Should().Contain("closing");
    }

    [Theory]
    [InlineData("name", "missing key")]
    [InlineData("key", "missing name")]
    [InlineData("description", "missing description")]
    public void Parse_RejectsMissingRequiredField(string presentField, string _)
    {
        // Build a frontmatter that has every field except `presentField`'s sibling so we can
        // assert exactly which field is reported as missing. (`presentField` here is the field
        // that REMAINS — the one we omit from the input is the one we expect to be flagged.)
        var fields = new Dictionary<string, string>
        {
            ["key"] = "alpha",
            ["name"] = "Alpha",
            ["description"] = "Alpha desc",
        };
        // Drop one field by re-keying: keep only `presentField` and exactly one other so the
        // parser sees a frontmatter that's missing the remaining required entry.
        // Simpler: omit each in turn explicitly.
        var omitted = presentField switch
        {
            "name" => "key",
            "key" => "name",
            "description" => "description",
            _ => throw new InvalidOperationException(),
        };

        var lines = new List<string> { "---" };
        foreach (var (k, v) in fields)
        {
            if (k == omitted) continue;
            lines.Add($"{k}: {v}");
        }
        lines.Add("---");
        lines.Add(string.Empty);
        lines.Add("Body.");
        var content = string.Join('\n', lines);

        var act = () => AssistantSkillParser.Parse("alpha.md", content);

        act.Should().Throw<InvalidSkillSourceException>()
            .Which.Message.Should().Contain($"'{omitted}'");
    }

    [Theory]
    [InlineData("Bad Key")]
    [InlineData("UPPER")]
    [InlineData("-leading-hyphen")]
    [InlineData("trailing space ")]
    [InlineData("under_score")]
    public void Parse_RejectsInvalidKeySlug(string key)
    {
        var content = $"---\nkey: {key}\nname: Alpha\ndescription: Alpha desc\n---\n\nBody.";

        var act = () => AssistantSkillParser.Parse("alpha.md", content);

        act.Should().Throw<InvalidSkillSourceException>()
            .Which.Message.Should().Contain("must match");
    }

    [Fact]
    public void Parse_RejectsEmptyBody()
    {
        var content = "---\nkey: alpha\nname: Alpha\ndescription: Alpha desc\n---\n";

        var act = () => AssistantSkillParser.Parse("alpha.md", content);

        act.Should().Throw<InvalidSkillSourceException>()
            .Which.Message.Should().Contain("body is empty");
    }

    [Fact]
    public void Provider_RejectsDuplicateKeyAcrossFiles()
    {
        var sources = new[]
        {
            ("first.md", "---\nkey: alpha\nname: Alpha\ndescription: Alpha desc\n---\nBody A."),
            ("second.md", "---\nkey: alpha\nname: Alpha 2\ndescription: Alpha desc 2\n---\nBody B."),
        };

        var act = () => new EmbeddedAssistantSkillProvider(sources);

        act.Should().Throw<InvalidSkillSourceException>()
            .Which.Message.Should().Contain("Duplicate skill key 'alpha'")
            .And.Contain("first.md");
    }

    [Fact]
    public void Provider_OrdersCatalogByKeyAndLooksUpByExactKey()
    {
        var sources = new[]
        {
            ("z.md", "---\nkey: zulu\nname: Zulu\ndescription: Zulu desc\n---\nZ body."),
            ("a.md", "---\nkey: alpha\nname: Alpha\ndescription: Alpha desc\n---\nA body."),
            ("m.md", "---\nkey: mike\nname: Mike\ndescription: Mike desc\n---\nM body."),
        };

        var provider = new EmbeddedAssistantSkillProvider(sources);

        provider.List().Select(s => s.Key).Should().Equal("alpha", "mike", "zulu");
        provider.Get("mike")!.Body.Should().Be("M body.");
        provider.Get("Mike").Should().BeNull(because: "key lookups are case-sensitive");
        provider.Get("nope").Should().BeNull();
    }
}
