using CodeFlow.Api.Assistant.Skills;
using FluentAssertions;

namespace CodeFlow.Api.Tests.Assistant.Skills;

/// <summary>
/// Structural lints for <c>runtime-vocabulary.md</c>. The legacy curated prompt covered every
/// runtime concept (traces, sagas, replay, drift detection, token tracking, code-aware /
/// working-directory layout) inline; AS-4 migrated that content into this skill. The asserts
/// below pin the load-bearing tokens so a future edit can't silently drop one.
/// </summary>
public sealed class RuntimeVocabularySkillTests
{
    private const string SkillKey = "runtime-vocabulary";

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
    // Runtime concept anchors that were lints on the legacy prompt and must survive the
    // migration into this skill.
    [InlineData("trace")]
    [InlineData("saga")]
    [InlineData("replay")]
    [InlineData("drift")]
    [InlineData("token")]
    [InlineData("in-place agent edit")]
    [InlineData("code-aware")]
    [InlineData("working directory")]
    public void Skill_CoversRuntimeConcepts(string concept)
    {
        LoadSkill().Body.Should().ContainEquivalentOf(concept);
    }

    [Fact]
    public void Skill_DescribesTokenUsageRecordShape()
    {
        // The legacy prompt taught the column tuple so the model could reason about what's
        // already aggregated server-side. Pin those load-bearing names.
        var body = LoadSkill().Body;
        body.Should().Contain("TokenUsageRecord");
        body.Should().Contain("traceId");
        body.Should().Contain("provider");
        body.Should().Contain("model");
    }

    [Fact]
    public void Skill_NeverPromisesDollarCosts()
    {
        // Project guardrail: CodeFlow tracks tokens only — never compute or display dollar
        // costs. This lint is paranoid because token-tracking content sits in this skill now;
        // a careless edit could reintroduce currency framing.
        var body = LoadSkill().Body.ToLowerInvariant();
        body.Should().NotContain("$");
        body.Should().NotContain("price per token");
        body.Should().NotContain("compute the cost");
        body.Should().NotContain("estimate the cost");
    }

    [Fact]
    public void Skill_ReferencesWorkingDirectoryRootSetting()
    {
        // The default workdir root and the Workspace__WorkingDirectoryRoot env-var override
        // are operator-facing facts the model will be asked about; pin them so the skill
        // can't drift from the platform's actual config keys.
        var body = LoadSkill().Body;
        body.Should().Contain("/app/codeflow/workdir");
        body.Should().Contain("Workspace__WorkingDirectoryRoot")
            .And.Subject.Should().Contain("WorkingDirectoryRoot");
    }
}
