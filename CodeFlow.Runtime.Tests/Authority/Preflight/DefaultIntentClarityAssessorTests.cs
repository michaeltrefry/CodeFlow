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

        result.IsClear.Should().BeTrue("phases 2/3 land later; until then unsupported modes pass through");
        result.OverallScore.Should().Be(1.0);
    }
}
