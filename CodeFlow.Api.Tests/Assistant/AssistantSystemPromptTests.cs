using CodeFlow.Api.Assistant;
using CodeFlow.Api.Assistant.Skills;
using FluentAssertions;

namespace CodeFlow.Api.Tests.Assistant;

/// <summary>
/// Structural lints for the trimmed base system prompt. AS-2 split the curated knowledge into
/// loadable skills; this file now covers ONLY the always-on baseline (identity, what CodeFlow
/// is, the safety/scope rules, the tool-surface mention, and the dynamically rendered skill
/// catalog). Per-skill structural lints land in their own test files alongside the skills they
/// guard (AS-3 / AS-4 / AS-5).
/// </summary>
public sealed class AssistantSystemPromptTests
{
    private static IAssistantSkillProvider EmptySkillProvider()
        => new EmbeddedAssistantSkillProvider(Array.Empty<(string, string)>());

    private static IAssistantSkillProvider SkillProviderWith(params (string FileName, string Content)[] sources)
        => new EmbeddedAssistantSkillProvider(sources);

    [Fact]
    public async Task DefaultProvider_RendersCatalogIntoBasePrompt()
    {
        var provider = new DefaultAssistantSystemPromptProvider(SkillProviderWith(
            ("a.md", "---\nkey: alpha\nname: Alpha\ndescription: Alpha desc\ntrigger: ta\n---\nA body."),
            ("b.md", "---\nkey: bravo\nname: Bravo\ndescription: Bravo desc\ntrigger: tb\n---\nB body.")));

        var prompt = await provider.GetSystemPromptAsync();

        prompt.Should().NotContain(AssistantSystemPrompt.SkillCatalogPlaceholder,
            because: "the placeholder must always be replaced before the prompt reaches the model");
        prompt.Should().Contain("- `alpha` — Alpha desc *Trigger:* ta");
        prompt.Should().Contain("- `bravo` — Bravo desc *Trigger:* tb");
    }

    [Fact]
    public void Default_RendersEmptyCatalogPlaceholder_WhenNoSkillsRegistered()
    {
        var prompt = AssistantSystemPrompt.Default;

        prompt.Should().NotContain(AssistantSystemPrompt.SkillCatalogPlaceholder);
        prompt.Should().Contain("No skills are currently registered",
            because: "the v1 baseline ships zero skill files; the catalog block must say so explicitly");
    }

    [Fact]
    public void DefaultPrompt_IsTrimmedFromTheLegacy700LineCuratedPrompt()
    {
        // AS-2 acceptance: the cacheable head of the system prompt is significantly smaller.
        // The legacy curated prompt was ~31 KB; the trimmed base + empty-catalog placeholder
        // should be well under 5 KB.
        var prompt = AssistantSystemPrompt.Default;
        prompt.Length.Should().BeGreaterThan(500,
            because: "a trivially small prompt means a section was deleted");
        prompt.Length.Should().BeLessThan(5_000,
            because: "AS-2 is a trim — domain knowledge moves into skills, not the base prompt");
    }

    [Fact]
    public void DefaultPrompt_IdentifiesTheAssistant()
    {
        var prompt = AssistantSystemPrompt.Default;
        prompt.Should().Contain("CodeFlow assistant");
        prompt.Should().Contain("workflow-orchestration");
    }

    [Fact]
    public void DefaultPrompt_ReinforcesWorkflowsAreData()
    {
        // Critical safety rule from project memory: the assistant should never tell users to
        // edit CodeFlow source code in order to author a workflow.
        AssistantSystemPrompt.Default.Should().Contain("Workflows are data");
    }

    [Theory]
    [InlineData("list_assistant_skills")]
    [InlineData("load_assistant_skill")]
    public void DefaultPrompt_NamesTheSkillTools(string toolName)
    {
        AssistantSystemPrompt.Default.Should().Contain(toolName,
            because: $"the base prompt must teach the model how to invoke '{toolName}'");
    }

    [Fact]
    public void DefaultPrompt_DescribesWhenToLoadSkills()
    {
        var prompt = AssistantSystemPrompt.Default;
        prompt.Should().Contain("When to load",
            because: "the base prompt must include the load-on-demand rules");
        prompt.Should().Contain("ONCE at the start of the domain",
            because: "users have hit token regressions when models reload skills mid-domain");
        prompt.Should().Contain("transcript and stays available",
            because: "the load-tool persistence rule must be explicit so the model doesn't re-call it");
    }

    [Fact]
    public void DefaultPrompt_FramesToolSurfaceAsExtensible()
    {
        // The operator can wire additional tools via an assigned agent role and an admin
        // overlay (operator instructions). Pin the explicit "use whichever tools the runtime
        // advertises" guidance and the <operator-instructions> handoff so future edits don't
        // reintroduce closed-set wording like "these are the only tools at your disposal".
        var prompt = AssistantSystemPrompt.Default;
        prompt.Should().ContainEquivalentOf("Use whichever tools the");
        prompt.Should().ContainEquivalentOf("runtime advertises");
        prompt.Should().Contain("Tools at your disposal",
            because: "the section header anchors the model's reading of the role-grant + operator-overlay handoff");
        prompt.Should().Contain("<operator-instructions>",
            because: "the curated prompt must point the model at the operator-overlay block");
        prompt.Should().NotContain("only tools at your disposal");
        prompt.Should().NotContain("these are the only tools",
            because: "the assistant's tool surface is extended by role grants + operator instructions");
    }

    [Fact]
    public void DefaultPrompt_NeverPromisesDollarCosts()
    {
        // Project guardrail: CodeFlow tracks tokens only — never compute or display dollar
        // costs. Catch any future edit that would reintroduce cost language.
        var prompt = AssistantSystemPrompt.Default.ToLowerInvariant();
        prompt.Should().NotContain("$");
        prompt.Should().NotContain("price per token");
        prompt.Should().NotContain("compute the cost");
        prompt.Should().NotContain("estimate the cost");
    }

    [Fact]
    public void RenderCatalog_EmptyList_IsExplicitAboutBeingEmpty()
    {
        var rendered = AssistantSystemPrompt.RenderCatalog(Array.Empty<AssistantSkill>());

        rendered.Should().Contain("No skills are currently registered");
    }

    [Fact]
    public void RenderCatalog_OneEntryPerSkill_OnSeparateLines()
    {
        var skills = new[]
        {
            new AssistantSkill("alpha", "Alpha", "Alpha desc", "alpha trigger", "A body."),
            new AssistantSkill("bravo", "Bravo", "Bravo desc", "bravo trigger", "B body."),
        };

        var rendered = AssistantSystemPrompt.RenderCatalog(skills);

        rendered.Should().Be(
            "- `alpha` — Alpha desc *Trigger:* alpha trigger\n"
            + "- `bravo` — Bravo desc *Trigger:* bravo trigger");
    }
}
