using CodeFlow.Api.Assistant.Skills;
using FluentAssertions;

namespace CodeFlow.Api.Tests.Assistant.Skills;

/// <summary>
/// Structural lints for <c>diagnose-trace.md</c>. The legacy curated prompt taught the
/// `diagnose_trace` invocation rules and the four-section diagnosis format inline; AS-4
/// migrated that content into this skill.
/// </summary>
public sealed class DiagnoseTraceSkillTests
{
    private const string SkillKey = "diagnose-trace";

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
    public void Skill_NamesTheDiagnoseTraceTool()
    {
        LoadSkill().Body.Should().Contain("diagnose_trace");
    }

    [Theory]
    // Anomaly heuristics surfaced by the diagnose tool — the LLM cites these by name when
    // explaining a trace's anomalies.
    [InlineData("long_duration")]
    [InlineData("token_spike")]
    [InlineData("logic_failure")]
    public void Skill_TeachesAnomalyHeuristics(string heuristic)
    {
        LoadSkill().Body.Should().Contain(heuristic);
    }

    [Theory]
    // Verdict-payload field names the diagnosis format pulls from. The skill must keep
    // teaching these so the model knows what's available in the response.
    [InlineData("summary")]
    [InlineData("deepLink")]
    [InlineData("agentDeepLink")]
    [InlineData("evidence")]
    [InlineData("suggestions")]
    public void Skill_TeachesVerdictPayloadFields(string field)
    {
        LoadSkill().Body.Should().Contain(field);
    }

    [Fact]
    public void Skill_ChainsGetNodeIoForFollowUps()
    {
        // Follow-up "show me node X's actual output" goes through `get_node_io`, not another
        // `diagnose_trace`. Pin the chaining rule so the skill can't drift into "always
        // re-diagnose" advice.
        var body = LoadSkill().Body;
        body.Should().Contain("get_node_io");
        body.Should().Contain("Do NOT re-invoke `diagnose_trace`");
    }
}
