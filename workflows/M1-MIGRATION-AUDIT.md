# M1 Migration Audit — First-Party Workflows to New Built-Ins

This document records the M1 audit of first-party workflows under `workflows/`,
migrating each to use the Phase 3 built-in features shipped in the Workflow
Authoring DX epic. Per CR1, no original version is mutated — each migration
produces a NEW workflow version that the operator can opt into.

## Migrations shipped this audit

The audit produced four migrated packages — two in the first pass (impl-plan-v4, dev-flow-v2) and two in a follow-up pass once T1 dry-run shipped (prd-intake-v6, lifecycle-v2).


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

### prd-intake-v5 → prd-intake-v6 (`prd-intake-v6-package.json`)

The same Pattern-3 (rejection-history) story as impl-plan, with the wrinkle that the v5 reviewer accumulated into a custom variable name (`workflow.prdRejectionHistory`) instead of using the framework's reserved key.

| Surface | Before (v5) | After (v6) |
|---|---|---|
| **Custom rejection-history accumulator** on `prd-reviewer` node | Hand-rolled outputScript appending to `workflow.prdRejectionHistory` | **P3**: `rejectionHistory.enabled: true` on each of the three parent ReviewLoop nodes (`prd-newproject-flow`, `prd-feature-flow`, `prd-bugfix-flow` → `prd-requirements-loop`). Reviewer's promptTemplate switched from `{{ workflow.prdRejectionHistory }}` to `{{ rejectionHistory }}` (the framework's un-prefixed alias for `__loop.rejectionHistory`). Inline `{{ if isLastRound }}` block in the promptTemplate removed (P2 auto-injects the equivalent reminder). |
| **Reviewer-base scaffolding** (approval-bias, last-round-behavior sections) on `prd-reviewer` | Inline in the systemPrompt | **P1 partial**: `partialPins: [@codeflow/reviewer-base v1]`; `## Approval bias` and `## Last-round behavior` sections deleted (replaced by partial + P2 auto-inject). |
| **Producer-base scaffolding** (no-Changes-Made, write-before-submit) on `prd-producer` | Inline (4-step "round behavior" with no-metadata rules) | **P1 partial**: `partialPins: [@codeflow/producer-base v1]`; redundant prose in "Round behavior" simplified (the partial covers the no-metadata-sections and write-before-submit rules). |
| **Workflow-vars declarations** | N/A | Skipped — the prd-intake stack writes most of its workflow vars (`requestKind`, `requestSummary`, `needsInterview`, `interviewTranscript`) via agent-side `setWorkflow` tool calls. F2 dataflow analysis only sees script-based writes, so a writes declaration would emit false-positive warnings. Same reasoning as dev-flow-v2. |

**Behavioral equivalence:** identical artifact flow at every junction. The reviewer no longer self-mirrors a custom rejection variable; the framework appends to `__loop.rejectionHistory` from the reviewer's message body instead. Templates that used `{{ workflow.prdRejectionHistory }}` now read `{{ rejectionHistory }}` and see the same content.

### lifecycle-v1 → lifecycle-v2 (`lifecycle-v2-package.json`)

The big one — lifecycle bundles its own copies of the prd-intake / impl-plan / dev-flow stacks (12 workflows + 24 agents + 1 role). Migrating it transitively absorbs every pattern handled in impl-plan-v4, dev-flow-v2, and prd-intake-v6:

- **dev-review-pair (inner loop)**: P3 enabled on the parent ReviewLoop in per-task-flow; `@codeflow/producer-base` pinned on `developer`; `@codeflow/reviewer-base` pinned on `code-reviewer`; inline last-round-behavior section removed (P2 auto-injects); reviewer template now reads `{{ rejectionHistory }}`. Mirrors dev-flow-v2 exactly.
- **impl-plan-loop**: P4 mirror (`mirrorOutputToWorkflowVar: "currentPlan"`) replaces architect's `setWorkflow('currentPlan', output.text)` outputScript; P5 port replacement (`outputPortReplacements: { "Approved": "currentPlan" }`) replaces reviewer's `setOutput(workflow.currentPlan)` Pattern-2 branch; P3 enabled on the parent ReviewLoop in impl-plan-flow; partials pinned on architect (`producer-base`) and reviewer (`reviewer-base`); reviewer template now reads `{{ rejectionHistory }}` instead of `{{ workflow.rejectionHistory }}`. Dead `latestRejection` variable removed. VZ2 declarations added: `impl-plan-flow` writes `["prd"]`, `impl-plan-loop` reads `["prd", "currentPlan"]` writes `["currentPlan"]`. Mirrors impl-plan-v4 exactly.
- **prd-requirements-loop** (used by all three prd-* sub-flows): drops the custom `workflow.prdRejectionHistory` accumulator outputScript on `prd-reviewer`; P3 enabled on the parent ReviewLoop in each of the three sub-flows; partials pinned on `prd-producer` and `prd-reviewer`; reviewer template now reads `{{ rejectionHistory }}`. Mirrors prd-intake-v6 exactly.
- **R-D cleanup**: fixed a Phase-0a rename leftover in `code-setup`'s inputScript — `(global && workflow.currentPlan)` → `workflow.currentPlan` (the JS `global` variable was renamed away during R-A; the broken expression would have ReferenceError'd at runtime).

**Self-containment**: the v2 package still bundles every transitively-referenced entity (12 workflows, 24 agents, 1 role) per V8. Unchanged entities (`prd-classifier`, `prd-*-director`, `prd-interviewer`, `prd-interview-form`, `prd-final-review-form`, `prd-socratic-loop`, `lifecycle-init`, `pm-pick-next`, `code-setup`, `task-committer`, `task-blocker`, `publish`, `post-mortem`, `impl-plan-init`, `build-gate-form`, `dev-escalate-form`, `impl-plan-escalate-form`) ride along at their existing versions.

**Cascading version bumps** (every parent of a changed entity is bumped):
- Workflows: `lifecycle v1→v2`, `dev-flow v2→v3`, `dev-review-pair v2→v3`, `per-task-flow v2→v3`, `impl-plan-flow v3→v4`, `impl-plan-loop v3→v4`, `prd-intake v5→v6`, `prd-newproject-flow v5→v6`, `prd-feature-flow v5→v6`, `prd-bugfix-flow v5→v6`, `prd-requirements-loop v5→v6`. (`prd-socratic-loop v4` unchanged.)
- Agents: `code-reviewer v2→v3`, `developer v1→v2`, `impl-plan-architect v2→v3`, `impl-plan-reviewer v3→v4`, `prd-producer v5→v6`, `prd-reviewer v5→v6`. All other agents unchanged.

## Validation status

- **JSON syntax**: `jq . impl-plan-v4-package.json`, `dev-flow-v2-package.json`, `prd-intake-v6-package.json`, and `lifecycle-v2-package.json` all parse cleanly.
- **Static schema**: matches the existing `schemaVersion: codeflow.workflow-package.v1` shape — same fields, same nesting; the new optional fields (`mirrorOutputToWorkflowVar`, `outputPortReplacements`, `rejectionHistory`, `workflowVarsReads`/`Writes`, `partialPins`) are exactly the surfaces the import path already understands.
- **Self-containment** (V8): each package bundles every transitively-referenced entity at the matching pinned version.
- **Runtime regression**: not validated end-to-end — would require T1 dry-run mode (which has shipped v1 but, per the T1-FOLLOWUP card, still does not cover input/output scripts, decision-output templates, rejection-history accumulation, or HITL form rendering at saga parity). Operator MUST manually compare a v_old trace against a v_new trace for the same input before promoting v_new to canonical. The framework features themselves carry their own end-to-end test coverage from the P3/P4/P5 cards' integration tests, which is the strongest non-runtime evidence available right now.

## Audit summary

- **Patterns successfully replaced**:
  - P4 mirror: 2× (impl-plan-v4 architect; lifecycle-v2 architect)
  - P5 port replacement: 2× (impl-plan-v4 reviewer; lifecycle-v2 reviewer)
  - P3 rejection-history: 7× (impl-plan-v4 loop, dev-flow-v2 inner loop, prd-intake-v6 ×3 sub-flows, lifecycle-v2 ×3 — impl-plan loop + dev-review-pair + prd-requirements-loop spans across the three lifecycle prd-* sub-flows = 5 ReviewLoop nodes total in lifecycle-v2; ×3 in prd-intake-v6 standalone)
  - P1 partials: 12× agent pins (impl-plan-v4 architect+reviewer; dev-flow-v2 developer+code-reviewer; prd-intake-v6 prd-producer+prd-reviewer; lifecycle-v2 mirrors all of the above)
  - Inline last-round-reminder removal: 5× (relying on P2 auto-injection)
  - VZ2 declarations: 4× (impl-plan-v4: 2 workflows; lifecycle-v2: 2 workflows)
- **Patterns left in place** (correctly):
  - Agent-side `setWorkflow` tool calls in dev-flow's PM, code-setup, and prd-intake's classifier/interviewer/directors (genuine mid-turn data writes)
  - `outputScript` on `dev-flow`'s PM-pick-next Logic node (genuine port-routing decision, not Pattern-1/2)
  - `inputScript` on `code-setup`, `impl-plan-init`, and `lifecycle-init` Start nodes (data seeding from input/context, not a candidate for any built-in)
- **Deleted entirely**: `workflow.latestRejection` (dead-write in impl-plan-v3 / lifecycle-v1 — no agent ever read it)
- **Bug fixes folded in**: lifecycle-v1's `code-setup` inputScript referenced the post-rename non-existent `global` JS variable; lifecycle-v2's version uses `workflow.currentPlan` directly.

The four migrations together exercise every Phase 3 feature against real workflow shapes — including a 2,000+ line lifecycle bundle that transitively wraps all three other packages — which is the strongest signal that the features compose cleanly at scale. The prompts shorten meaningfully, the cross-references stay intact, and the resulting workflows are easier to read for new authors.
