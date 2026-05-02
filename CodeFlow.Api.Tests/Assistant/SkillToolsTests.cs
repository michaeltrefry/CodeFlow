using System.Text.Json;
using CodeFlow.Api.Assistant.Skills;
using CodeFlow.Api.Assistant.Tools;
using FluentAssertions;

namespace CodeFlow.Api.Tests.Assistant;

/// <summary>
/// Unit tests for the on-demand skill tools (<c>list_assistant_skills</c>,
/// <c>load_assistant_skill</c>). The tools are provider-driven, so each test wires up a small
/// in-memory provider through the test-only constructor.
/// </summary>
public sealed class SkillToolsTests
{
    private static readonly JsonElement EmptyArgs = JsonDocument.Parse("{}").RootElement;

    private static JsonElement Args(object obj) => JsonSerializer.SerializeToElement(obj);

    private static IAssistantSkillProvider ProviderWith(params (string FileName, string Content)[] sources)
        => new EmbeddedAssistantSkillProvider(sources);

    private static (string FileName, string Content) Skill(string key, string name, string description, string trigger, string body)
        => ($"{key}.md",
            $"---\nkey: {key}\nname: {name}\ndescription: {description}\ntrigger: {trigger}\n---\n{body}");

    private static JsonElement ParseObject(AssistantToolResult result)
    {
        result.IsError.Should().BeFalse(because: result.ResultJson);
        using var doc = JsonDocument.Parse(result.ResultJson);
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task ListAssistantSkills_EmptyProvider_ReturnsEmptyArray()
    {
        var tool = new ListAssistantSkillsTool(ProviderWith());

        var result = ParseObject(await tool.InvokeAsync(EmptyArgs, CancellationToken.None));

        result.GetProperty("count").GetInt32().Should().Be(0);
        result.GetProperty("skills").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task ListAssistantSkills_ReturnsKeyNameDescriptionTrigger_ButNotBody()
    {
        var tool = new ListAssistantSkillsTool(ProviderWith(
            Skill("alpha", "Alpha", "Alpha desc", "alpha trigger", "A body."),
            Skill("bravo", "Bravo", "Bravo desc", "bravo trigger", "B body.")));

        var result = ParseObject(await tool.InvokeAsync(EmptyArgs, CancellationToken.None));

        result.GetProperty("count").GetInt32().Should().Be(2);
        var rows = result.GetProperty("skills").EnumerateArray()
            .Select(r => new
            {
                Key = r.GetProperty("key").GetString(),
                Name = r.GetProperty("name").GetString(),
                Description = r.GetProperty("description").GetString(),
                Trigger = r.GetProperty("trigger").GetString(),
                HasBody = r.TryGetProperty("body", out _),
            })
            .ToArray();

        rows.Should().HaveCount(2);
        rows[0].Key.Should().Be("alpha");
        rows[0].Name.Should().Be("Alpha");
        rows[0].Description.Should().Be("Alpha desc");
        rows[0].Trigger.Should().Be("alpha trigger");
        rows[0].HasBody.Should().BeFalse(because: "the catalog must not pay the body cost");
        rows[1].Key.Should().Be("bravo");
    }

    [Fact]
    public async Task LoadAssistantSkill_ReturnsKeyAndBody()
    {
        var tool = new LoadAssistantSkillTool(ProviderWith(
            Skill("alpha", "Alpha", "Alpha desc", "alpha trigger", "Full body content here.")));

        var result = ParseObject(await tool.InvokeAsync(Args(new { key = "alpha" }), CancellationToken.None));

        result.GetProperty("key").GetString().Should().Be("alpha");
        result.GetProperty("body").GetString().Should().Be("Full body content here.");
    }

    [Fact]
    public async Task LoadAssistantSkill_UnknownKey_ReturnsErrorWithAvailableKeys()
    {
        var tool = new LoadAssistantSkillTool(ProviderWith(
            Skill("alpha", "Alpha", "Alpha desc", "alpha trigger", "A body."),
            Skill("bravo", "Bravo", "Bravo desc", "bravo trigger", "B body.")));

        var result = await tool.InvokeAsync(Args(new { key = "nope" }), CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.ResultJson.Should().Contain("nope");
        result.ResultJson.Should().Contain("alpha");
        result.ResultJson.Should().Contain("bravo");
    }

    [Fact]
    public async Task LoadAssistantSkill_EmptyCatalog_ErrorMentionsNoSkillsRegistered()
    {
        var tool = new LoadAssistantSkillTool(ProviderWith());

        var result = await tool.InvokeAsync(Args(new { key = "anything" }), CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.ResultJson.Should().Contain("No skills are currently registered");
    }

    [Fact]
    public async Task LoadAssistantSkill_MissingKeyArgument_ReturnsError()
    {
        var tool = new LoadAssistantSkillTool(ProviderWith(
            Skill("alpha", "Alpha", "Alpha desc", "alpha trigger", "A body.")));

        var result = await tool.InvokeAsync(EmptyArgs, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.ResultJson.Should().Contain("key");
    }
}
