namespace CodeFlow.Runtime.Authority.Preflight;

/// <summary>
/// sc-274 — deterministic intent-clarity assessor. Producers call <see cref="Assess"/>
/// with a mode-specific input shape; the assessor returns an <see cref="IntentClarityAssessment"/>
/// that the caller turns into a refusal (when not clear) or lets through (when clear).
///
/// The interface accepts <see cref="object"/> so each mode's heuristic can interpret the
/// payload it knows how to score (e.g. <see cref="ReplayEditPreflightInput"/> for the
/// replay endpoint). Unsupported payload shapes for the mode produce a missing-input
/// refusal — the assessor never throws on unrecognized inputs.
/// </summary>
public interface IIntentClarityAssessor
{
    IntentClarityAssessment Assess(PreflightMode mode, object input);
}

/// <summary>
/// Replay-edit preflight input. Captures the shape the replay endpoint passes to the
/// assessor — mirrors the request DTO but stays in the runtime layer so the assessor
/// doesn't depend on <c>CodeFlow.Api</c>.
/// </summary>
/// <param name="Edits">User-supplied edits. May be empty.</param>
/// <param name="HasAdditionalMocks">True when the request supplies extra mock responses.</param>
/// <param name="HasWorkflowVersionOverride">True when the request pins a different workflow version.</param>
public sealed record ReplayEditPreflightInput(
    IReadOnlyList<ReplayEditPreflightEdit> Edits,
    bool HasAdditionalMocks,
    bool HasWorkflowVersionOverride);

/// <param name="AgentKey">Agent the edit targets.</param>
/// <param name="Ordinal">Per-agent ordinal within the recorded trace.</param>
/// <param name="Decision">Decision string the edit substitutes (null = no override).</param>
/// <param name="Output">Output string the edit substitutes (null = no override).</param>
/// <param name="HasPayload">
/// True when the edit supplied a non-null payload JSON node. We only need the boolean
/// for scoring; the payload's content isn't inspected by the deterministic heuristics.
/// </param>
public sealed record ReplayEditPreflightEdit(
    string AgentKey,
    int Ordinal,
    string? Decision,
    string? Output,
    bool HasPayload);
