using CodeFlow.Api.Assistant.Skills;
using FluentAssertions;

namespace CodeFlow.Api.Tests.Assistant.Skills;

/// <summary>
/// Structural lints for <c>redacted-payload-handling.md</c>. The skill teaches the model what
/// the <c>_redacted: true</c> placeholder means in its transcript history and how to act on
/// the actual current state when it sees one. The asserts below pin every load-bearing token
/// — placeholder shape, do-not-copy rule, the routes back to fresh state — so a future edit
/// can't silently drop one and let the model fall back to copying the stub.
/// </summary>
public sealed class RedactedPayloadHandlingSkillTests
{
    private const string SkillKey = "redacted-payload-handling";

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

    [Theory]
    // Stub-shape anchors. The runtime emits exactly these field names on the redacted payload;
    // the skill must keep teaching them so the model recognises the stub on sight.
    [InlineData("_redacted")]
    [InlineData("sha256")]
    [InlineData("sizeBytes")]
    [InlineData("summary")]
    [InlineData("workflowCount")]
    [InlineData("nodeCount")]
    [InlineData("agentCount")]
    [InlineData("entryPoint")]
    public void Skill_TeachesStubShape(string token)
    {
        LoadSkill().Body.Should().Contain(token);
    }

    [Fact]
    public void Skill_GivesExplicitDoNotCopyDirective()
    {
        var body = LoadSkill().Body;
        body.Should().ContainEquivalentOf("Do NOT copy",
            because: "the do-not-copy directive is the load-bearing instruction this skill exists for");
        body.Should().Contain("rejection",
            because: "the skill must teach that the tools refuse the stub at entry");
    }

    [Theory]
    // Routes back to the current state — every channel the model must know about so it doesn't
    // get stuck reading a placeholder.
    [InlineData("get_workflow_package_draft")]
    [InlineData("patch_workflow_package_draft")]
    [InlineData("save_workflow_package")]
    [InlineData("get_workflow_package")]
    public void Skill_NamesEveryRouteBackToTheCurrentState(string toolName)
    {
        LoadSkill().Body.Should().Contain(toolName);
    }

    [Fact]
    public void Skill_ExplainsTheNEqualsOneBuffer()
    {
        // The N=1 invariant is the reason older carriers get demoted; the model needs the
        // mental model to interpret what it sees in its own transcript.
        var body = LoadSkill().Body;
        body.Should().ContainEquivalentOf("at most one",
            because: "the N=1 buffer is the runtime invariant that motivates the stub");
        body.Should().Contain("transcript-only",
            because: "the skill must reassure the model that the on-disk draft is unaffected — that's why fetching again is cheap");
    }
}
