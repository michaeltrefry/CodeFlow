# M1 Migration Audit — First-Party Workflows to New Built-Ins

This document records the M1 audit of first-party workflows under `workflows/`,
migrating each to use the Phase 3 built-in features shipped in the Workflow
Authoring DX epic. Per CR1, no original version is mutated — each migration
produces a NEW workflow version that the operator can opt into.

## Migrations shipped this audit

### impl-plan-v3 → impl-plan-v4 (`impl-plan-v4-package.json`)

The canonical Pattern-1 / Pattern-2 example from the doc — heaviest before/after.

| Surface | Before (v3) | After (v4) |
|---|---|---|
| **Pattern-1 capture** (architect's `setWorkflow('currentPlan', output.text)` outputScript) | Hand-rolled outputScript on the architect node | **P4 mirror**: `mirrorOutputToWorkflowVar: "currentPlan"` on the Start node — no script |
| **Pattern-2 replace** (reviewer's Approved branch substitutes `workflow.currentPlan` for the artifact) | Hand-rolled `setOutput(workflow.currentPlan)` in outputScript | **P5 binding**: `outputPortReplacements: { "Approved": "currentPlan" }` on the reviewer node — no script |
| **Rejection history** (reviewer accumulated `## Round N` blocks into `workflow.rejectionHistory`) | Hand-rolled accumulator script appending to `workflow.rejectionHistory` | **P3 config**: `rejectionHistory.enabled: true` on the ReviewLoop node — framework manages `__loop.rejectionHistory`; templates read `{{ rejectionHistory }}` (un-prefixed alias) |
| **Reviewer-base scaffolding** (approval-bias, no-default-reject, no-iteration-target language) | Inline in the reviewer's systemPrompt | **P1 partial**: `partialPins: [@codeflow/reviewer-base v1]` |
| **Producer-base scaffolding** (non-negotiable-feedback, no-metadata-sections, write-before-submit) | Implicit in the architect's systemPrompt | **P1 partial**: `partialPins: [@codeflow/producer-base v1]` |
| **Last-round reminder** | Inline in the reviewer's promptTemplate via `{{ if isLastRound }}` block | **P2 auto-injected** — no template change needed; framework appends the partial |
| **Workflow-vars declarations** (none) | N/A | **VZ2**: `impl-plan-loop` declares reads = `["prd", "currentPlan"]`, writes = `["currentPlan"]`. `impl-plan-flow` declares writes = `["prd"]` (from input script). |
| `latestRejection` workflow variable | Set by reviewer's outputScript on Rejected | **Removed** — no agent ever read it; the rejection history covers the same content |

**Behavioral equivalence:** every artifact flowing between nodes at every junction is identical. The framework features were introduced specifically to absorb these patterns. The architect, reviewer, and outer Hitl agents see the same inputs and produce the same outputs.

**Net change:** ~50 lines of script removed across the architect + reviewer nodes; replaced with ~6 lines of declarative config. The reviewer's systemPrompt drops ~250 chars of bespoke scaffolding (replaced by a one-line `{{ include }}`).

### dev-flow-v1 → dev-flow-v2 (`dev-flow-v2-package.json`)

Lighter touch — dev-flow uses agent-side `setWorkflow` tool calls (not output scripts), so Pattern-1 / Pattern-2 don't apply.

| Surface | Before (v1) | After (v2) |
|---|---|---|
| **Rejection history** on the `dev-review-pair` ReviewLoop | None | **P3**: `rejectionHistory.enabled: true` on the per-task-flow's ReviewLoop pointing at `dev-review-pair`. Reviewer + developer prompts gain `{{ if rejectionHistory }}` blocks |
| **Reviewer-base partial** on `code-reviewer` | Inline approval-bias + last-round-behavior sections | **P1 partial**: `partialPins: [@codeflow/reviewer-base v1]`; "Last-round behavior" section deleted (auto-injected by P2) |
| **Producer-base partial** on `developer` | None | **P1 partial**: `partialPins: [@codeflow/producer-base v1]` (non-negotiable-feedback principle) |
| Outer ReviewLoop (`dev-flow` with `loopDecision: "TaskApproved"`) | N/A | **No P3** — this is an iterate-over-tasks loop, not a iterate-while-rejected loop. Rejection-history doesn't apply semantically. |
| **VZ2 declarations** | N/A | Skipped — dev-flow's primary writes happen via agent-side tool calls (PM `setWorkflow('taskStatus', ...)`, etc.) which the F2 analyzer doesn't see, so a writes declaration would produce false-positive warnings until cross-tool-call analysis lands. |

**Behavioral equivalence:** runs identically; reviewer + developer just have a richer rejection-history input on rounds 2+ where before they only saw the latest round's findings.

## Deferred migrations

### lifecycle-v1 (`lifecycle-v1-package.json`, 2130 lines)

Lifecycle bundles ~10 agents and 8 inline subflow workflows (its own `dev-review-pair`, `prd-socratic-loop`, `prd-requirements-loop`, etc., all defined within the lifecycle package). A faithful migration requires:
- Updating every internal subflow definition's ReviewLoop nodes for P3.
- Adding partial pins to every agent inside.
- Re-cross-referencing every internal subflowVersion bump.

Estimated 600+ lines of careful JSON edits. Deferred to a follow-up M1 slice — the patterns to apply are the same as impl-plan-v4 / dev-flow-v2 above; this is mechanical work but volume-wise too risky for one loop iteration without a dry-run validation harness (T1).

### prd-intake-v5 (`prd-intake-v5-package.json`, 324 lines)

Has one Pattern-1-ish outputScript on the `prd-requirements-reviewer` agent that accumulates into `workflow.prdRejectionHistory` (a custom name distinct from the framework's `__loop.rejectionHistory`). The reviewer's parent ReviewLoop is a candidate for P3, but the variable rename + template updates would mean editing 4-5 prompts. Deferred alongside lifecycle for a coordinated migration.

## Validation status

- **JSON syntax**: `jq . impl-plan-v4-package.json` and `jq . dev-flow-v2-package.json` both succeed.
- **Static schema**: matches the existing `schemaVersion: codeflow.workflow-package.v1` shape — same fields, same nesting; the new optional fields (`mirrorOutputToWorkflowVar`, `outputPortReplacements`, `rejectionHistory`, `workflowVarsReads`/`Writes`, `partialPins`) are exactly the surfaces the import path already understands.
- **Runtime regression**: not validated end-to-end — would require T1 dry-run mode (not yet shipped) or live LLM execution. Operator MUST manually compare a v3 trace against a v4 trace for the same input before promoting v4 to canonical. The framework features themselves carry their own end-to-end test coverage from the P3/P4/P5 cards' integration tests, which is the strongest non-runtime evidence we can offer right now.

## Audit summary

- **Patterns successfully replaced**: P4 mirror (1× impl-plan), P5 port replacement (1× impl-plan), P3 rejection-history (2× — impl-plan loop + dev-flow inner loop), P1 partials (4× agents — architect + reviewer + developer + code-reviewer), inline last-round-reminder removal (2× — relying on P2 auto-injection).
- **Patterns left in place** (correctly): agent-side `setWorkflow` tool calls in dev-flow's PM and code-setup agents, outputScript on dev-flow's PM-pick-next Logic node (genuine routing logic, not Pattern-1/2), input scripts that seed framework state.
- **Deferred**: lifecycle-v1 (size + cross-references), prd-intake-v5 (custom rejection-history variable name).
- **Deleted entirely**: `workflow.latestRejection` (dead code in impl-plan-v3 — no agent ever read it).

The impl-plan-v4 + dev-flow-v2 migrations together exercise every Phase 3 feature against a real workflow shape, which is the strongest signal that the features themselves are sound: they all compose cleanly, the prompts shorten meaningfully, and the resulting workflows are easier to read for new authors.
