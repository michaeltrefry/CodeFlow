using CodeFlow.Api.Assistant.Skills;
using FluentAssertions;

namespace CodeFlow.Api.Tests.Assistant.Skills;

/// <summary>
/// Structural lints for <c>replay-with-edit.md</c>. The legacy curated prompt taught the
/// `propose_replay_with_edit` invocation, the four-branch result handling, and the
/// substitution-only constraint inline; AS-4 migrated that content into this skill.
/// </summary>
public sealed class ReplayWithEditSkillTests
{
    private const string SkillKey = "replay-with-edit";

    private static AssistantSkill LoadSkill()
    {
        var skill = new EmbeddedAssistantSkillProvider().Get(SkillKey);
        skill.Should().NotBeNull(
            because: $"the embedded resource pipeline must surface '{SkillKey}.md'");
        return skill!;
    }

    [Fact]
    public void Skill_ExposesExpectedFrontmatter()
    {
        var skill = LoadSkill();
        skill.Key.Should().Be(SkillKey);
        skill.Name.Should().NotBeNullOrWhiteSpace();
        skill.Description.Should().NotBeNullOrWhiteSpace();
        skill.Trigger.Should().NotBeNullOrWhiteSpace();
        skill.Body.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Skill_NamesTheProposeReplayTool()
    {
        LoadSkill().Body.Should().Contain("propose_replay_with_edit");
    }

    [Theory]
    [InlineData("agentKey")]
    [InlineData("ordinal")]
    [InlineData("decision")]
    [InlineData("output")]
    [InlineData("payload")]
    public void Skill_TeachesEditEntryShape(string field)
    {
        LoadSkill().Body.Should().Contain(field);
    }

    [Theory]
    // Every result-status branch the LLM may see and must handle distinctly.
    [InlineData("preview_ok")]
    [InlineData("\"invalid\"")]
    [InlineData("unsupported")]
    [InlineData("trace_not_found")]
    public void Skill_DescribesResultBranches(string statusToken)
    {
        LoadSkill().Body.Should().Contain(statusToken);
    }

    [Fact]
    public void Skill_ReinforcesSubstitutionOnlyConstraint()
    {
        var body = LoadSkill().Body;
        body.Should().ContainEquivalentOf("substitution-only",
            because: "the substitution-only constraint is the load-bearing rule the user must understand");
        body.Should().Contain("cannot",
            because: "the skill must explicitly call out what Replay-with-Edit can NOT do");
    }

    [Fact]
    public void Skill_CallsOutSwarmNonReplayability()
    {
        // Swarm nodes always re-execute fresh on replay; replay-with-edit cannot substitute
        // their cached outputs. Pin this so the skill keeps teaching the user the right
        // expectation.
        LoadSkill().Body.Should().ContainEquivalentOf("Swarm");
    }
}
