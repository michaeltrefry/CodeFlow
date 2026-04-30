using System.Text.RegularExpressions;

namespace CodeFlow.Runtime.Authority.Preflight;

/// <summary>
/// sc-274 phase 1 — deterministic intent-clarity assessor. Implements heuristics for
/// <see cref="PreflightMode.ReplayEdit"/>; later phases extend the same class with
/// additional mode handlers (homepage assistant, workflow launch, PRD drafting).
///
/// Heuristics intentionally avoid model calls: the assessor is meant to short-circuit
/// before any token spend, so its scoring uses field presence, length thresholds, and
/// shape-collision rules. Each heuristic that fires lowers the relevant dimension score
/// AND appends an actionable clarification question — the dimension score is the
/// machine-readable signal, the question is what the UI shows the author.
/// </summary>
public sealed class DefaultIntentClarityAssessor : IIntentClarityAssessor
{
    private static readonly Regex PlaceholderPattern = new(
        @"\b(todo|fixme|tbd|xxx|lorem|ipsum|placeholder|wip)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private const int VagueOutputMinLength = 24;

    private readonly PreflightOptions options;

    public DefaultIntentClarityAssessor(PreflightOptions? options = null)
    {
        this.options = options ?? new PreflightOptions();
    }

    public IntentClarityAssessment Assess(PreflightMode mode, object input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return mode switch
        {
            PreflightMode.ReplayEdit when input is ReplayEditPreflightInput replay => AssessReplayEdit(replay),
            // Phases 2/3 land here. Until then, an unsupported mode means "no preflight" — return
            // a fully clear assessment so the caller can let the work through unchanged.
            _ => new IntentClarityAssessment(
                Mode: mode,
                OverallScore: 1.0,
                Threshold: ThresholdFor(mode),
                IsClear: true,
                Dimensions: Array.Empty<IntentClarityDimensionScore>(),
                MissingFields: Array.Empty<string>(),
                ClarificationQuestions: Array.Empty<string>()),
        };
    }

    private IntentClarityAssessment AssessReplayEdit(ReplayEditPreflightInput input)
    {
        var threshold = ThresholdFor(PreflightMode.ReplayEdit);
        // Goal scoring is intentionally a no-op for replay edits: round-trip identity
        // (zero edits, no mocks, no override) is a documented legitimate path used to
        // verify timing / observability of an unchanged replay. The other dimensions
        // catch the actually ambiguous cases (decision-only edits, placeholders, collisions).
        var goalScore = 1.0;
        var contextScore = 1.0;
        var constraintsScore = 1.0;
        var successScore = 1.0;
        const string? goalReason = null;
        string? contextReason = null;
        string? constraintsReason = null;
        string? successReason = null;
        var missing = new List<string>();
        var questions = new List<string>();

        // Constraints — conflicting edits target the same agent+ordinal.
        var collisionKeys = input.Edits
            .GroupBy(e => (e.AgentKey, e.Ordinal))
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key.AgentKey}/{g.Key.Ordinal}")
            .ToArray();
        if (collisionKeys.Length > 0)
        {
            constraintsScore = Math.Min(constraintsScore, 0.0);
            constraintsReason = $"two edits collide at the same agent+ordinal: {string.Join(", ", collisionKeys)}";
            foreach (var key in collisionKeys)
            {
                missing.Add($"edits[{key}].unique");
            }
            questions.Add($"Two edits collide at the same agent+ordinal ({string.Join(", ", collisionKeys)}). Which edit should win?");
        }

        // Success criteria — Decision changed but no Output AND no Payload (no new evidence).
        // Walk edits with stable indices so missing-field paths are useful in the UI.
        for (var i = 0; i < input.Edits.Count; i++)
        {
            var edit = input.Edits[i];
            var hasDecisionOverride = !string.IsNullOrWhiteSpace(edit.Decision);
            var hasOutput = !string.IsNullOrWhiteSpace(edit.Output);
            if (hasDecisionOverride && !hasOutput && !edit.HasPayload)
            {
                successScore = Math.Min(successScore, 0.4);
                successReason ??= "decision-changing edit provided no output or payload, so the dry-run can't observe what the agent would have produced";
                missing.Add($"edits[{i}].output");
                questions.Add($"Edit at {edit.AgentKey}/ord-{edit.Ordinal} changes the decision to '{edit.Decision}' but provides no output or payload. What should the agent's output have been?");
            }
        }

        // Context — vague output (very short and contains placeholder markers like TODO/FIXME/lorem).
        for (var i = 0; i < input.Edits.Count; i++)
        {
            var edit = input.Edits[i];
            if (string.IsNullOrWhiteSpace(edit.Output))
            {
                continue;
            }

            var trimmed = edit.Output.Trim();
            var looksPlaceholder = PlaceholderPattern.IsMatch(trimmed);
            var tooShort = trimmed.Length < VagueOutputMinLength;
            if (looksPlaceholder && tooShort)
            {
                contextScore = Math.Min(contextScore, 0.4);
                contextReason ??= "edit output is short and contains placeholder text — replay would consume a stub instead of realistic agent output";
                missing.Add($"edits[{i}].output.placeholder");
                questions.Add($"Edit at {edit.AgentKey}/ord-{edit.Ordinal} looks like a placeholder ('{Truncate(trimmed, 32)}'). Replace it with realistic agent output before re-running.");
            }
        }

        var dimensions = new[]
        {
            new IntentClarityDimensionScore(IntentClarityDimensions.Goal, goalScore, goalReason),
            new IntentClarityDimensionScore(IntentClarityDimensions.Constraints, constraintsScore, constraintsReason),
            new IntentClarityDimensionScore(IntentClarityDimensions.SuccessCriteria, successScore, successReason),
            new IntentClarityDimensionScore(IntentClarityDimensions.Context, contextScore, contextReason),
        };

        var overall = dimensions.Min(d => d.Score);
        return new IntentClarityAssessment(
            Mode: PreflightMode.ReplayEdit,
            OverallScore: overall,
            Threshold: threshold,
            IsClear: overall >= threshold,
            Dimensions: dimensions,
            MissingFields: missing,
            ClarificationQuestions: questions);
    }

    private double ThresholdFor(PreflightMode mode) => mode switch
    {
        PreflightMode.ReplayEdit => options.ReplayEditThreshold,
        PreflightMode.AssistantChat => options.AssistantChatThreshold,
        PreflightMode.BrownfieldChange => options.BrownfieldChangeThreshold,
        PreflightMode.GreenfieldDraft => options.GreenfieldDraftThreshold,
        _ => options.ReplayEditThreshold,
    };

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}

/// <summary>
/// Thresholds + kill-switch for <see cref="DefaultIntentClarityAssessor"/>. Modes that
/// haven't shipped yet still carry a default so phase 2/3 wiring is a config-only step.
/// </summary>
public sealed class PreflightOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Replay-edit threshold (phase 1). Conservative default — any major dimension miss refuses.</summary>
    public double ReplayEditThreshold { get; set; } = 0.5;

    /// <summary>Reserved for phase 2.</summary>
    public double AssistantChatThreshold { get; set; } = 0.4;

    /// <summary>Reserved for phase 3.</summary>
    public double BrownfieldChangeThreshold { get; set; } = 0.6;

    /// <summary>Reserved for phase 3.</summary>
    public double GreenfieldDraftThreshold { get; set; } = 0.7;
}
