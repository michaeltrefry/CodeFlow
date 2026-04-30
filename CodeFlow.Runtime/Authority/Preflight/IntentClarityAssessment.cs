namespace CodeFlow.Runtime.Authority.Preflight;

/// <summary>
/// sc-274 — deterministic ambiguity preflight for freeform input. Producers score the
/// input across four dimensions (goal, constraints, success criteria, context) and emit
/// a list of focused clarification questions when any dimension falls below the mode's
/// threshold.
///
/// The assessment is the contract — the producer is <see cref="IIntentClarityAssessor"/>,
/// and consumers (replay endpoint today; assistant chat / workflow launch later) decide
/// what to do with a non-clear assessment. A non-clear assessment in v1 means the work
/// is refused with a <see cref="RefusalStages.Preflight"/> refusal event, the
/// clarification questions are returned to the caller, and no execution starts.
/// </summary>
/// <param name="Mode">Which entry-point thresholds were applied.</param>
/// <param name="OverallScore">
/// 0.0 (totally ambiguous) to 1.0 (fully specified). Computed as the minimum of the four
/// dimension scores so a single missing dimension is enough to refuse — refining any one
/// dimension lifts the floor in proportion to how much it improves clarity.
/// </param>
/// <param name="Threshold">
/// The minimum <see cref="OverallScore"/> the mode requires for <see cref="IsClear"/> to
/// be true. Carried alongside the score so callers can show "62% — needs 70%" without a
/// separate config lookup.
/// </param>
/// <param name="IsClear">
/// True when <see cref="OverallScore"/> ≥ <see cref="Threshold"/>. Producers that want to
/// allow execution despite a non-clear preflight can override at the endpoint level —
/// the assessment itself is purely descriptive.
/// </param>
/// <param name="Dimensions">Per-dimension scores + reasons.</param>
/// <param name="MissingFields">
/// Specific fields the assessor expected to see populated but found null/empty/vague.
/// Stable, machine-readable identifiers (e.g. <c>edit[0].output</c>); the UI uses these
/// to highlight inputs.
/// </param>
/// <param name="ClarificationQuestions">
/// Ordered list of focused questions to ask the caller. Generated deterministically from
/// the heuristic that fired — no model calls involved. The first question is the most
/// blocking; the UI may surface only the top one initially.
/// </param>
public sealed record IntentClarityAssessment(
    PreflightMode Mode,
    double OverallScore,
    double Threshold,
    bool IsClear,
    IReadOnlyList<IntentClarityDimensionScore> Dimensions,
    IReadOnlyList<string> MissingFields,
    IReadOnlyList<string> ClarificationQuestions);

public sealed record IntentClarityDimensionScore(
    string Dimension,
    double Score,
    string? Reason);

/// <summary>
/// Canonical dimension names. The four dimensions mirror the Protostar intent-clarity
/// frame; producers should reuse these constants so consumer UIs and governance queries
/// can rely on stable identifiers.
/// </summary>
public static class IntentClarityDimensions
{
    /// <summary>What is the caller trying to accomplish?</summary>
    public const string Goal = "goal";

    /// <summary>What boundaries / non-goals / risks should we respect?</summary>
    public const string Constraints = "constraints";

    /// <summary>How will we know the work succeeded?</summary>
    public const string SuccessCriteria = "success_criteria";

    /// <summary>What background does the assessor need to interpret the request?</summary>
    public const string Context = "context";
}

/// <summary>
/// Entry-point classification — each mode carries its own threshold + heuristic set so a
/// concise replay edit (low-risk, scoped) is judged differently from a greenfield PRD
/// generation (high-risk, open-ended).
/// </summary>
public enum PreflightMode
{
    /// <summary>POST /api/traces/{id}/replay edits — sc-274 phase 1.</summary>
    ReplayEdit,

    /// <summary>Homepage assistant chat prompts — sc-274 phase 2 (planned).</summary>
    AssistantChat,

    /// <summary>Brownfield code-change workflows (e.g. dev/reviewer loop) — phase 3 (planned).</summary>
    BrownfieldChange,

    /// <summary>Greenfield workflow / PRD drafting — phase 3 (planned).</summary>
    GreenfieldDraft,
}
