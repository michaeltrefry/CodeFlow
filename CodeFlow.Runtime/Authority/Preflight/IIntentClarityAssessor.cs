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

/// <summary>
/// sc-274 phase 2 — homepage assistant chat preflight input. The freeform user message
/// plus the page-context shape the client sent. Page context is captured as
/// presence/kind/selection booleans rather than the full record so the assessor stays in
/// the runtime layer and doesn't depend on <c>CodeFlow.Api</c>'s <c>AssistantPageContext</c>.
/// </summary>
/// <param name="Content">The user-typed prompt. Trimmed by the caller before the assessor runs.</param>
/// <param name="HasPageContext">
/// True when the client sent any page context at all. False means the user is on the homepage
/// or has no current entity selection — pronouns like "this" / "it" cannot be implicitly
/// resolved by the model.
/// </param>
/// <param name="PageContextKind">
/// The <c>kind</c> field from the page context (e.g. <c>trace</c>, <c>workflow-editor</c>,
/// <c>agent-editor</c>, <c>library</c>). Used to decide whether pronouns can resolve — a
/// kind like <c>home</c> or <c>traces-list</c> doesn't pin a specific entity.
/// </param>
public sealed record AssistantChatPreflightInput(
    string Content,
    bool HasPageContext,
    string? PageContextKind);

/// <summary>
/// sc-274 phase 3 — workflow launch preflight input. The freeform Start-agent input plus a
/// signal the caller derives from the launch request (presence of the <c>repositories</c>
/// context input) so the assessor can route to the brownfield-vs-greenfield heuristic set
/// without depending on workflow metadata. <c>WorkflowKey</c> is carried so refusal events
/// can attribute the refusal to a specific workflow without parsing the detail blob.
/// </summary>
/// <param name="Input">
/// The freeform Start-agent input the user supplied (the <c>input</c> field on
/// <c>POST /api/traces</c>). Trimmed by the caller before the assessor runs.
/// </param>
/// <param name="HasRepositoriesInput">
/// True when the launch request supplied a <c>repositories</c> key in <c>Inputs</c>. This
/// is CodeFlow's de-facto convention for code-aware (brownfield) workflows; absent it,
/// the workflow is assumed to be greenfield (drafting from scratch). The caller picks the
/// <see cref="PreflightMode"/> based on this flag, not the assessor.
/// </param>
/// <param name="WorkflowKey">
/// The workflow being launched. Carried for refusal-event attribution; not used by the
/// heuristics themselves.
/// </param>
public sealed record WorkflowLaunchPreflightInput(
    string Input,
    bool HasRepositoriesInput,
    string? WorkflowKey);
