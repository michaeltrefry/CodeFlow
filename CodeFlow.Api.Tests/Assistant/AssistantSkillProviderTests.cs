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
    private const string MinimalFrontmatter =
        "key: alpha\nname: Alpha\ndescription: Alpha desc\ntrigger: alpha trigger\n";

    private static string MinimalSkillSource(string body = "Body.")
        => $"---\n{MinimalFrontmatter}---\n\n{body}";

    [Fact]
    public void EmptySources_ProducesEmptyCatalog()
    {
        var provider = new EmbeddedAssistantSkillProvider(Array.Empty<(string, string)>());

        provider.List().Should().BeEmpty();
        provider.Get("anything").Should().BeNull();
    }

    [Fact]
    public void Parse_ExtractsKeyNameDescriptionTriggerAndBody()
    {
        var content = """
            ---
            key: workflow-authoring
            name: Workflow authoring
            description: Use when drafting or saving workflow JSON.
            trigger: user wants to author or edit a workflow.
            ---

            Body line 1.

            Body line 2.
            """;

        var skill = AssistantSkillParser.Parse("test.md", content);

        skill.Key.Should().Be("workflow-authoring");
        skill.Name.Should().Be("Workflow authoring");
        skill.Description.Should().Be("Use when drafting or saving workflow JSON.");
        skill.Trigger.Should().Be("user wants to author or edit a workflow.");
        skill.Body.Should().Be("Body line 1.\n\nBody line 2.");
    }

    [Fact]
    public void Parse_NormalizesCrlfLineEndings()
    {
        var content = "---\r\nkey: alpha\r\nname: Alpha\r\ndescription: Alpha desc\r\n"
            + "trigger: alpha trigger\r\n---\r\n\r\nBody.\r\n";

        var skill = AssistantSkillParser.Parse("alpha.md", content);

        skill.Body.Should().Be("Body.");
        skill.Trigger.Should().Be("alpha trigger");
    }

    [Fact]
    public void Parse_ToleratesLeadingBlankLines()
    {
        var content = "\n\n" + MinimalSkillSource();

        var skill = AssistantSkillParser.Parse("alpha.md", content);

        skill.Key.Should().Be("alpha");
        skill.Body.Should().Be("Body.");
    }

    [Fact]
    public void Parse_RejectsMissingFrontmatterDelimiter()
    {
        var content = MinimalFrontmatter + "\nBody.";

        var act = () => AssistantSkillParser.Parse("alpha.md", content);

        act.Should().Throw<InvalidSkillSourceException>()
            .Which.Message.Should().Contain("frontmatter delimiter");
    }

    [Fact]
    public void Parse_RejectsUnclosedFrontmatter()
    {
        var content = "---\n" + MinimalFrontmatter + "\nBody (but no closing ---)";

        var act = () => AssistantSkillParser.Parse("alpha.md", content);

        act.Should().Throw<InvalidSkillSourceException>()
            .Which.Message.Should().Contain("closing");
    }

    [Theory]
    [InlineData("key")]
    [InlineData("name")]
    [InlineData("description")]
    [InlineData("trigger")]
    public void Parse_RejectsMissingRequiredField(string omitted)
    {
        // Build a frontmatter with every required field except `omitted`, then assert the parser
        // identifies the missing field by name in its error.
        var fields = new Dictionary<string, string>
        {
            ["key"] = "alpha",
            ["name"] = "Alpha",
            ["description"] = "Alpha desc",
            ["trigger"] = "Alpha trigger",
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
        var content = $"---\nkey: {key}\nname: Alpha\ndescription: Alpha desc\ntrigger: t\n---\n\nBody.";

        var act = () => AssistantSkillParser.Parse("alpha.md", content);

        act.Should().Throw<InvalidSkillSourceException>()
            .Which.Message.Should().Contain("must match");
    }

    [Fact]
    public void Parse_RejectsEmptyBody()
    {
        var content = "---\n" + MinimalFrontmatter + "---\n";

        var act = () => AssistantSkillParser.Parse("alpha.md", content);

        act.Should().Throw<InvalidSkillSourceException>()
            .Which.Message.Should().Contain("body is empty");
    }

    [Fact]
    public void Provider_RejectsDuplicateKeyAcrossFiles()
    {
        var sources = new[]
        {
            ("first.md", MinimalSkillSource("Body A.")),
            ("second.md",
                "---\nkey: alpha\nname: Alpha 2\ndescription: Alpha desc 2\ntrigger: t2\n---\nBody B."),
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
            ("z.md", "---\nkey: zulu\nname: Zulu\ndescription: Zulu desc\ntrigger: tz\n---\nZ body."),
            ("a.md", "---\nkey: alpha\nname: Alpha\ndescription: Alpha desc\ntrigger: ta\n---\nA body."),
            ("m.md", "---\nkey: mike\nname: Mike\ndescription: Mike desc\ntrigger: tm\n---\nM body."),
        };

        var provider = new EmbeddedAssistantSkillProvider(sources);

        provider.List().Select(s => s.Key).Should().Equal("alpha", "mike", "zulu");
        provider.Get("mike")!.Body.Should().Be("M body.");
        provider.Get("mike")!.Trigger.Should().Be("tm");
        provider.Get("Mike").Should().BeNull(because: "key lookups are case-sensitive");
        provider.Get("nope").Should().BeNull();
    }
}
