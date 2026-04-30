using CodeFlow.Runtime.Authority.Preflight;
using FluentAssertions;

namespace CodeFlow.Runtime.Tests.Authority.Preflight;

/// <summary>
/// sc-274 phase 1 — covers the deterministic heuristics in <see cref="DefaultIntentClarityAssessor"/>
/// against the four firing conditions for replay-edit mode plus a clear baseline.
/// </summary>
public sealed class DefaultIntentClarityAssessorTests
{
    private static DefaultIntentClarityAssessor Assessor() =>
        new DefaultIntentClarityAssessor(new PreflightOptions());

    [Fact]
    public void Replay_EmptyRequest_PassesAsRoundTripIdentity()
    {
        // Round-trip identity (re-run the trace verbatim) is a legitimate documented path
        // for replay-with-edit — preflight should not refuse it. The goal dimension is
        // intentionally a no-op for replay edits in v1.
        var input = new ReplayEditPreflightInput(
            Edits: Array.Empty<ReplayEditPreflightEdit>(),
            HasAdditionalMocks: false,
            HasWorkflowVersionOverride: false);

        var result = Assessor().Assess(PreflightMode.ReplayEdit, input);

        result.IsClear.Should().BeTrue();
        result.OverallScore.Should().Be(1.0);
        result.MissingFields.Should().BeEmpty();
        result.ClarificationQuestions.Should().BeEmpty();
    }

    [Fact]
    public void Replay_DecisionChangedWithoutOutput_FlagsSuccessCriteria()
    {
        var input = new ReplayEditPreflightInput(
            Edits: new[]
            {
                new ReplayEditPreflightEdit("agent-a", 0, Decision: "Failed", Output: null, HasPayload: false),
            },
            HasAdditionalMocks: false,
            HasWorkflowVersionOverride: false);

        var result = Assessor().Assess(PreflightMode.ReplayEdit, input);

        result.IsClear.Should().BeFalse();
        var success = result.Dimensions.Single(d => d.Dimension == IntentClarityDimensions.SuccessCriteria);
        success.Score.Should().BeLessThan(0.5);
        success.Reason.Should().NotBeNull();
        result.MissingFields.Should().Contain(f => f.Contains("output"));
        result.ClarificationQuestions.Should().Contain(q => q.Contains("agent-a/ord-0"));
    }

    [Fact]
    public void Replay_VagueOutput_FlagsContext()
    {
        var input = new ReplayEditPreflightInput(
            Edits: new[]
            {
                new ReplayEditPreflightEdit("agent-a", 0, Decision: "Completed", Output: "TODO", HasPayload: false),
            },
            HasAdditionalMocks: false,
            HasWorkflowVersionOverride: false);

        var result = Assessor().Assess(PreflightMode.ReplayEdit, input);

        result.IsClear.Should().BeFalse();
        var context = result.Dimensions.Single(d => d.Dimension == IntentClarityDimensions.Context);
        context.Score.Should().Be(0.4);
        result.MissingFields.Should().Contain(f => f.Contains("placeholder"));
    }

    [Fact]
    public void Replay_CollidingEdits_FlagsConstraints()
    {
        var input = new ReplayEditPreflightInput(
            Edits: new[]
            {
                new ReplayEditPreflightEdit("agent-a", 0, "Completed", "first output longer than minimum", false),
                new ReplayEditPreflightEdit("agent-a", 0, "Failed", "second output longer than minimum", false),
            },
            HasAdditionalMocks: false,
            HasWorkflowVersionOverride: false);

        var result = Assessor().Assess(PreflightMode.ReplayEdit, input);

        result.IsClear.Should().BeFalse();
        var constraints = result.Dimensions.Single(d => d.Dimension == IntentClarityDimensions.Constraints);
        constraints.Score.Should().Be(0.0);
        result.MissingFields.Should().Contain(f => f.Contains("edits[agent-a/0].unique"));
    }

    [Fact]
    public void Replay_ConcreteWellFormedEdit_PassesPreflight()
    {
        var input = new ReplayEditPreflightInput(
            Edits: new[]
            {
                new ReplayEditPreflightEdit(
                    "agent-a", 0, "Completed",
                    Output: "Generated PRD draft v2 covering the migration scope and rollback plan.",
                    HasPayload: false),
            },
            HasAdditionalMocks: false,
            HasWorkflowVersionOverride: false);

        var result = Assessor().Assess(PreflightMode.ReplayEdit, input);

        result.IsClear.Should().BeTrue();
        result.OverallScore.Should().Be(1.0);
        result.MissingFields.Should().BeEmpty();
        result.ClarificationQuestions.Should().BeEmpty();
    }

    [Fact]
    public void UnsupportedMode_ReturnsClearAssessment()
    {
        var result = Assessor().Assess(
            PreflightMode.GreenfieldDraft,
            new ReplayEditPreflightInput(Array.Empty<ReplayEditPreflightEdit>(), false, false));

        result.IsClear.Should().BeTrue("phase 3 lands later; until then unsupported modes pass through");
        result.OverallScore.Should().Be(1.0);
    }

    // ===== sc-274 phase 2 — AssistantChat heuristics =====
    //
    // The AssistantChat surface is the highest-base-rate freeform input, so the heuristics
    // are deliberately narrow: only refuse on placeholder text, or on action-verb requests
    // that lack any scope to act on. Question-shaped or info-request prompts always pass
    // through unchanged.

    [Theory]
    [InlineData("What is the current trace state?")]
    [InlineData("Explain how the auth flow works")]
    [InlineData("Hi, can you help me?")]
    [InlineData("Thanks!")]
    [InlineData("Tell me about CodeFlow")]
    [InlineData("show the failing tests")]
    [InlineData("how does the saga work")]
    public void AssistantChat_QuestionsAndInfoRequests_PassThrough(string content)
    {
        var input = new AssistantChatPreflightInput(content, HasPageContext: false, PageContextKind: null);

        var result = Assessor().Assess(PreflightMode.AssistantChat, input);

        result.IsClear.Should().BeTrue();
        result.OverallScore.Should().Be(1.0);
        result.ClarificationQuestions.Should().BeEmpty();
    }

    [Theory]
    [InlineData("TODO")]
    [InlineData("FIXME")]
    [InlineData("?")]
    [InlineData("...")]
    [InlineData("asdf")]
    public void AssistantChat_PurePlaceholders_RefuseWithGoalZero(string content)
    {
        var input = new AssistantChatPreflightInput(content, HasPageContext: false, PageContextKind: null);

        var result = Assessor().Assess(PreflightMode.AssistantChat, input);

        result.IsClear.Should().BeFalse();
        result.OverallScore.Should().Be(0.0);
        result.MissingFields.Should().Contain("content.placeholder");
        result.ClarificationQuestions.Should().ContainSingle()
            .Which.Should().Contain("What would you like help with?");
    }

    [Theory]
    [InlineData("fix it")]
    [InlineData("make it better")]
    [InlineData("do that")]
    [InlineData("improve this")]
    [InlineData("refactor that")]
    [InlineData("do the thing")]
    [InlineData("make stuff")]
    public void AssistantChat_VagueActionVerb_RefusesWithGoalAndQuestion(string content)
    {
        var input = new AssistantChatPreflightInput(content, HasPageContext: false, PageContextKind: null);

        var result = Assessor().Assess(PreflightMode.AssistantChat, input);

        result.IsClear.Should().BeFalse();
        var goal = result.Dimensions.Single(d => d.Dimension == IntentClarityDimensions.Goal);
        goal.Score.Should().Be(0.2);
        goal.Reason.Should().NotBeNull();
        result.MissingFields.Should().Contain("content.scope");
        result.ClarificationQuestions.Should().Contain(q => q.StartsWith("What specifically should I"));
    }

    [Fact]
    public void AssistantChat_VagueActionWithPronoun_AddsPronounClarification()
    {
        // "fix it" trips both heuristics (vague-action AND pronoun-without-context). The
        // paired clarification ("which trace/workflow/agent?") only piggy-backs on the
        // vague-action refusal — it doesn't fire as its own heuristic, so refining the
        // first part of the prompt also clears the pronoun question.
        var input = new AssistantChatPreflightInput("fix it", HasPageContext: false, PageContextKind: null);

        var result = Assessor().Assess(PreflightMode.AssistantChat, input);

        result.IsClear.Should().BeFalse();
        result.MissingFields.Should().Contain("content.pronoun-without-context");
        result.ClarificationQuestions.Should().HaveCount(2);
        result.ClarificationQuestions.Should().Contain(q => q.Contains("trace, workflow, or agent"));
    }

    [Theory]
    [InlineData("trace")]
    [InlineData("workflow-editor")]
    [InlineData("agent-editor")]
    public void AssistantChat_PronounWithEntityScopedPageContext_PassesThrough(string pageContextKind)
    {
        // "fix this" with the user viewing a specific trace / editing a workflow / editing an
        // agent — the model can resolve "this" implicitly. Vague-action heuristic still fires
        // ("fix" + ≤2 words + pronoun), but the pronoun-paired clarification doesn't add
        // a second question because the page context resolves the reference.
        var input = new AssistantChatPreflightInput(
            "fix this", HasPageContext: true, PageContextKind: pageContextKind);

        var result = Assessor().Assess(PreflightMode.AssistantChat, input);

        // Vague-action still refuses (pronoun-resolution doesn't add a scope noun).
        result.IsClear.Should().BeFalse();
        result.MissingFields.Should().NotContain("content.pronoun-without-context");
        result.ClarificationQuestions.Should().NotContain(q => q.Contains("trace, workflow, or agent"));
    }

    [Theory]
    [InlineData("fix the bug in CodeFlow.Api/Assistant/CodeFlowAssistant.cs")]
    [InlineData("update the AssistantChatService to log token usage")]
    [InlineData("refactor the dev/reviewer workflow")]
    [InlineData("write a new transform node template")]
    [InlineData("add the user-management module")] // hyphenated identifier counts as scope
    public void AssistantChat_ActionWithScope_PassesThrough(string content)
    {
        var input = new AssistantChatPreflightInput(content, HasPageContext: false, PageContextKind: null);

        var result = Assessor().Assess(PreflightMode.AssistantChat, input);

        result.IsClear.Should().BeTrue();
        result.OverallScore.Should().Be(1.0);
        result.ClarificationQuestions.Should().BeEmpty();
    }

    [Fact]
    public void AssistantChat_EmptyMessage_PassesThroughForUpstreamValidationToCatch()
    {
        // Empty content is rejected with HTTP 400 BadRequest by the endpoint before preflight
        // runs; the assessor itself returns clear so it doesn't shadow the more specific
        // upstream error if a caller bypasses validation.
        var input = new AssistantChatPreflightInput("   ", HasPageContext: false, PageContextKind: null);

        var result = Assessor().Assess(PreflightMode.AssistantChat, input);

        result.IsClear.Should().BeTrue();
    }
}
