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
            PreflightMode.AssistantChat when input is AssistantChatPreflightInput chat => AssessAssistantChat(chat),
            PreflightMode.BrownfieldChange when input is WorkflowLaunchPreflightInput launch => AssessBrownfieldChange(launch),
            PreflightMode.GreenfieldDraft when input is WorkflowLaunchPreflightInput launch => AssessGreenfieldDraft(launch),
            // Unsupported mode / wrong input shape — pass through. The assessor never throws on
            // unrecognized payloads; a configuration mistake should not block production work.
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

    /// <summary>
    /// sc-274 phase 2 — assistant chat is the highest-base-rate freeform surface, so the
    /// heuristic set is intentionally narrow: only refuse when the message LOOKS like an
    /// action request (imperative verb at start) but lacks enough scope to act on. Question-
    /// shaped prompts, info requests ("explain", "describe"), greetings, and casual
    /// acknowledgements all pass through unchanged. False-positives feel naggy here in a way
    /// they don't on the replay endpoint, so we under-refuse rather than over-refuse.
    /// </summary>
    private IntentClarityAssessment AssessAssistantChat(AssistantChatPreflightInput input)
    {
        var threshold = ThresholdFor(PreflightMode.AssistantChat);
        var trimmed = (input.Content ?? string.Empty).Trim();

        // Empty / whitespace messages are caught by request validation upstream (400 BadRequest).
        // Treat as clear here so the assessor doesn't swallow the more specific upstream error.
        if (trimmed.Length == 0)
        {
            return ClearAssessment(PreflightMode.AssistantChat, threshold);
        }

        // Pure-placeholder check runs FIRST so a message that's literally "TODO" or "FIXME"
        // refuses regardless of question shape. Placeholders are the strongest "this isn't
        // a real prompt" signal.
        if (PurePlaceholderPattern.IsMatch(trimmed))
        {
            var dims = new[]
            {
                new IntentClarityDimensionScore(IntentClarityDimensions.Goal, 0.0,
                    "message is a placeholder token (TODO/FIXME/TBD/WIP) — no actual request to act on"),
                Clear(IntentClarityDimensions.Constraints),
                Clear(IntentClarityDimensions.SuccessCriteria),
                Clear(IntentClarityDimensions.Context),
            };
            return new IntentClarityAssessment(
                Mode: PreflightMode.AssistantChat,
                OverallScore: 0.0,
                Threshold: threshold,
                IsClear: false,
                Dimensions: dims,
                MissingFields: ["content.placeholder"],
                ClarificationQuestions: ["What would you like help with?"]);
        }

        // Skip preflight for question-shaped or info-request prompts. The model handles those
        // fine without a scope; they're the dominant case and refusing them would feel naggy.
        if (LooksLikeQuestionOrInfoRequest(trimmed))
        {
            return ClearAssessment(PreflightMode.AssistantChat, threshold);
        }

        var goalScore = 1.0;
        var contextScore = 1.0;
        string? goalReason = null;
        string? contextReason = null;
        var missing = new List<string>();
        var questions = new List<string>();

        var firstWord = ExtractFirstWord(trimmed);
        var startsWithActionVerb = firstWord is not null && ActionVerbs.Contains(firstWord);
        var wordCount = CountWords(trimmed);

        // Vague action — imperative verb at start, no scope noun, AND one of: very short
        // (≤2 words), uses a vague pronoun (this/it/that), or uses a placeholder noun
        // (thing/stuff). Each of those signals tells us "this isn't actionable yet"; without
        // them, plain noun phrases like "build a website" are left for the model to handle
        // (it will either generate or ask its own clarifying question — the preflight gate
        // is for the cases where there's nothing for the model to even start with).
        if (startsWithActionVerb && !ContainsScopeNoun(trimmed))
        {
            var veryShort = wordCount <= 2;
            var hasPronoun = ContainsPronounReference(trimmed);
            var hasPlaceholderNoun = PlaceholderNounPattern.IsMatch(trimmed);

            if (veryShort || hasPronoun || hasPlaceholderNoun)
            {
                goalScore = 0.2;
                goalReason = $"action verb '{firstWord}' with no specific scope (file, component, role, or named entity)";
                missing.Add("content.scope");
                questions.Add($"What specifically should I {firstWord}? Name a file, component, workflow, or trace.");

                // Paired clarification — when the message also uses a vague pronoun and the
                // page context can't resolve it, surface that too. Doesn't fire as its own
                // heuristic (false-positive risk on conversational replies like "this is
                // broken" mid-thread); only piggy-backs on the vague-action refusal.
                if (hasPronoun && !PageContextResolvesPronouns(input))
                {
                    contextScore = 0.4;
                    contextReason = "message uses 'this' / 'it' / 'that' but no page context pins which entity you mean";
                    missing.Add("content.pronoun-without-context");
                    questions.Add("Which trace, workflow, or agent are you referring to? Open it and ask again, or tell me the id.");
                }
            }
        }

        var dimensions = new[]
        {
            new IntentClarityDimensionScore(IntentClarityDimensions.Goal, goalScore, goalReason),
            Clear(IntentClarityDimensions.Constraints),
            Clear(IntentClarityDimensions.SuccessCriteria),
            new IntentClarityDimensionScore(IntentClarityDimensions.Context, contextScore, contextReason),
        };

        var overall = dimensions.Min(d => d.Score);
        return new IntentClarityAssessment(
            Mode: PreflightMode.AssistantChat,
            OverallScore: overall,
            Threshold: threshold,
            IsClear: overall >= threshold,
            Dimensions: dimensions,
            MissingFields: missing,
            ClarificationQuestions: questions);
    }

    /// <summary>
    /// sc-274 phase 3 — brownfield code-change workflow launch (e.g. dev/reviewer loop).
    /// The workflow has the repo for context, so the heuristic only needs to make sure the
    /// freeform Start-agent input expresses the change goal in enough words to act on. Single-
    /// or three-word Inputs ("review PR", "fix the build") are the dominant under-specified
    /// shape and refuse with a goal-dimension miss.
    /// </summary>
    private IntentClarityAssessment AssessBrownfieldChange(WorkflowLaunchPreflightInput input) =>
        AssessWorkflowLaunch(
            input,
            mode: PreflightMode.BrownfieldChange,
            tooShortWordCount: 3,
            tooShortQuestion: "What change are you asking for? Describe the goal in 1-2 sentences.");

    /// <summary>
    /// sc-274 phase 3 — greenfield workflow / PRD drafting launch. The workflow has no repo to
    /// anchor on, so the input itself has to carry the goal + audience + success criteria. The
    /// bar is correspondingly stricter: anything under five words almost certainly leaves the
    /// drafting agent with nothing to design against.
    /// </summary>
    private IntentClarityAssessment AssessGreenfieldDraft(WorkflowLaunchPreflightInput input) =>
        AssessWorkflowLaunch(
            input,
            mode: PreflightMode.GreenfieldDraft,
            tooShortWordCount: 4,
            tooShortQuestion: "Describe what you want drafted in at least 1-2 sentences (the goal, who it's for, why it matters).");

    /// <summary>
    /// Shared shape for the two workflow-launch modes — they only differ in the word-count
    /// floor and the clarification wording. Pure placeholder input refuses with a goal score
    /// of 0.0 in both modes; the empty-input case is caught by the endpoint's required-field
    /// validation upstream and passes through here so the assessor doesn't shadow the more
    /// specific upstream error.
    /// </summary>
    private IntentClarityAssessment AssessWorkflowLaunch(
        WorkflowLaunchPreflightInput input,
        PreflightMode mode,
        int tooShortWordCount,
        string tooShortQuestion)
    {
        var threshold = ThresholdFor(mode);
        var trimmed = (input.Input ?? string.Empty).Trim();

        if (trimmed.Length == 0)
        {
            return ClearAssessment(mode, threshold);
        }

        if (PurePlaceholderPattern.IsMatch(trimmed))
        {
            var dims = new[]
            {
                new IntentClarityDimensionScore(IntentClarityDimensions.Goal, 0.0,
                    "input is a placeholder token (TODO/FIXME/TBD/WIP) — no actual goal to act on"),
                Clear(IntentClarityDimensions.Constraints),
                Clear(IntentClarityDimensions.SuccessCriteria),
                Clear(IntentClarityDimensions.Context),
            };
            return new IntentClarityAssessment(
                Mode: mode,
                OverallScore: 0.0,
                Threshold: threshold,
                IsClear: false,
                Dimensions: dims,
                MissingFields: ["input.placeholder"],
                ClarificationQuestions: ["What would you like the workflow to do?"]);
        }

        var wordCount = CountWords(trimmed);
        if (wordCount <= tooShortWordCount)
        {
            var dims = new[]
            {
                new IntentClarityDimensionScore(IntentClarityDimensions.Goal, 0.2,
                    $"input is {wordCount} word(s) — not enough to act on for a {ModeLabel(mode)} launch"),
                Clear(IntentClarityDimensions.Constraints),
                Clear(IntentClarityDimensions.SuccessCriteria),
                Clear(IntentClarityDimensions.Context),
            };
            return new IntentClarityAssessment(
                Mode: mode,
                OverallScore: 0.2,
                Threshold: threshold,
                IsClear: false,
                Dimensions: dims,
                MissingFields: ["input.too-short"],
                ClarificationQuestions: [tooShortQuestion]);
        }

        return ClearAssessment(mode, threshold);
    }

    private static string ModeLabel(PreflightMode mode) => mode switch
    {
        PreflightMode.BrownfieldChange => "brownfield code-change",
        PreflightMode.GreenfieldDraft => "greenfield drafting",
        _ => mode.ToString(),
    };

    private static IntentClarityDimensionScore Clear(string dimension) =>
        new(dimension, 1.0, null);

    private static IntentClarityAssessment ClearAssessment(PreflightMode mode, double threshold) =>
        new(
            Mode: mode,
            OverallScore: 1.0,
            Threshold: threshold,
            IsClear: true,
            Dimensions: Array.Empty<IntentClarityDimensionScore>(),
            MissingFields: Array.Empty<string>(),
            ClarificationQuestions: Array.Empty<string>());

    private static readonly Regex PurePlaceholderPattern = new(
        @"^(todo|fixme|tbd|wip|xxx|placeholder|test|asdf|asdfgh|\?+|\.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Unambiguous interrogatives + info-request verbs. These signal a question or
    /// information request even without a trailing <c>?</c>. Kept separate from the auxiliary
    /// pattern below because words like "do" are also imperatives ("do that") — those need
    /// the pronoun follow-up to be recognized as questions.
    /// </summary>
    private static readonly Regex InterrogativeStartPattern = new(
        @"^(what|why|how|when|where|who|whom|which|whose|explain|describe|tell|show|list|find|search|summari[sz]e|recap|recall|read|view|look|help)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Auxiliaries that are only questions when paired with a subject pronoun. "Do you know"
    /// is a question; "do that" is an imperative. The trailing <c>?</c> short-circuit catches
    /// any phrasing this pattern misses.
    /// </summary>
    private static readonly Regex AuxiliaryQuestionPattern = new(
        @"^(can|could|would|should|do|does|did|is|are|was|were|will|may|might)\s+(you|we|i|they|he|she|it)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PronounReferencePattern = new(
        @"\b(this|that|it|these|those)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Tokens that stand in for an unspecified noun ("do the thing", "make the stuff work").
    /// They look like nouns to a part-of-speech check but carry no scope, so paired with an
    /// action verb they're a refusal signal.
    /// </summary>
    private static readonly Regex PlaceholderNounPattern = new(
        @"\b(thing|things|stuff|something|anything|whatever)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex WordPattern = new(
        @"[\p{L}\p{N}_]+", RegexOptions.Compiled);

    /// <summary>
    /// Imperative verbs that signal "do something". We deliberately keep this list short and
    /// concrete — adding too many verbs (especially polysemous ones like "run" / "show") would
    /// catch info-requests too. "Run" is intentionally absent because "run X" is usually
    /// scope-bearing; it shows up via the question/info-request bypass instead.
    /// </summary>
    private static readonly HashSet<string> ActionVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "fix", "make", "change", "update", "refactor", "improve", "write", "add", "remove",
        "delete", "build", "rename", "rewrite", "redo", "do",
    };

    /// <summary>
    /// Tokens that mark the message as carrying a concrete scope — file extensions, path
    /// separators, identifier-like CamelCase or snake_case tokens, or recognized CodeFlow
    /// concept nouns. If any of these appear, the message has enough surface area to act on
    /// even if the verb is vague.
    /// </summary>
    private static readonly Regex ScopeNounPattern = new(
        @"(\.[a-z0-9]{1,5}\b|/[a-z0-9_\-/.]+|[A-Z][a-z]+[A-Z]|[a-z]+_[a-z]+|\b(trace|workflow|agent|node|edge|port|saga|tool|endpoint|schema|migration|test|component|service|repository|entity|controller|model|prompt|template|role|policy|envelope|gate|loop|hub|router|chip|panel|page|view|column|table|index|query|api)\b)",
        RegexOptions.Compiled);

    private static bool LooksLikeQuestionOrInfoRequest(string trimmed)
    {
        if (trimmed.EndsWith('?'))
        {
            return true;
        }
        return InterrogativeStartPattern.IsMatch(trimmed) || AuxiliaryQuestionPattern.IsMatch(trimmed);
    }

    private static bool ContainsPronounReference(string trimmed) =>
        PronounReferencePattern.IsMatch(trimmed);

    private static bool ContainsScopeNoun(string trimmed) =>
        ScopeNounPattern.IsMatch(trimmed);

    /// <summary>
    /// Page contexts that pin a single entity ("the trace I'm viewing", "the workflow I'm
    /// editing") let the model resolve pronouns implicitly. List-style or homepage contexts
    /// don't, so pronouns dangle.
    /// </summary>
    private static bool PageContextResolvesPronouns(AssistantChatPreflightInput input)
    {
        if (!input.HasPageContext || string.IsNullOrWhiteSpace(input.PageContextKind))
        {
            return false;
        }
        return input.PageContextKind switch
        {
            "trace" or "workflow-editor" or "agent-editor" => true,
            _ => false,
        };
    }

    private static string? ExtractFirstWord(string trimmed)
    {
        var match = WordPattern.Match(trimmed);
        return match.Success ? match.Value.ToLowerInvariant() : null;
    }

    private static int CountWords(string trimmed) =>
        WordPattern.Matches(trimmed).Count;

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
