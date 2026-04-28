# Swarm Bench Harness (Phase 0)

**Epic:** [Self-Organizing Agents (Swarm Node) Exploration](https://app.shortcut.com/trefry/epic/38) (sc-38)
**Story:** [P0] Define bench harness — [sc-39](https://app.shortcut.com/trefry/story/39)
**Status:** Design doc — not yet executable
**Source paper:** *Self-Organizing LLM Agents* — https://arxiv.org/html/2603.28990v1

This doc defines the comparison harness that gates Phase 1 (hand-authored Sequential subflow) and Phase 2 (Swarm node) of the epic. Without it, the head-to-head numbers in sc-41 and sc-44 aren't apples-to-apples.

## What we're comparing

Three variants, same input, same rubric:

| Variant | Phase | Source |
|---|---|---|
| **V1 — Single-agent baseline** | P0 (authored as part of harness) | New `swarm-bench-baseline-v1` workflow library entry: `Start → Agent → Done`. Single AgentRole, generalist prompt, same model + budget as V2/V3. |
| **V2 — Hand-authored Sequential subflow** | P1 (sc-40) | New `swarm-bench-sequential-v1` workflow library entry: Subflow with N=4 Agent nodes in series. Each agent's system prompt asks it to (a) examine prior contributions in globals, (b) self-select the role most useful to the mission, (c) contribute or abstain, (d) (final agent only) synthesize. Threading via `outputScript` writing to globals (per `feedback_workflow_globals_via_scripts`). |
| **V3 — Swarm node, `protocol=Sequential`** | P2 (sc-43) | Runtime node, non-replayable. Same N=4, same agent pool config as V2 (TBD in sc-42 design). |

**Why N=4:** Paper sweeps N ∈ {4, 8, 16, 32, 64}; effect sizes on Sequential are already large at N=4 and our cost ceiling makes 8+ expensive on multi-request bench. Revisit N after first read.

**Model fixing:** All three variants use `claude-sonnet-4-6` for v1 of the bench. Rerun on `claude-opus-4-7` if results are ambiguous. Don't mix models within a variant.

## Knobs you can vary without code changes

Almost everything interesting about this bench is data, not runtime code. Use this table when you want to sweep a parameter:

| Knob | Where it lives | How to change |
|---|---|---|
| **Rubric** (the five scoring dimensions) | This doc + the scorer's CSV columns | Edit doc + CSV header. Pure judging artifact — the runtime never sees it. |
| **Request shapes** (Request A, Request B) | `bench/swarm/requests/A.md`, `B.md` | Edit the markdown. Re-run. |
| **Number of runs per cell** (N=3) | Harness procedure only | Trigger more traces, append rows with new `run_index`. |
| **Number of agents in V2** (N=4 in the Sequential subflow) | The `swarm-bench-sequential-v1` workflow JSON package | Add or remove Agent nodes in the package, bump version, re-import. No code change. |
| **Number of agents in V3** (the Swarm node) | Node-config field on the runtime Swarm node (designed in sc-42, lands in sc-43) | Edit the node config. Once V3 ships, this is also pure data. |
| **Per-agent system prompt** (the "self-select role / abstain if not useful" instruction) | The `AgentRole` definition each Agent node references; or inline on the node via the prompt-template system | Edit the AgentRole or template. Scriban-aware, sandboxed. |
| **Model** (`claude-sonnet-4-6`) | `AgentRole` config | Change the model field on the role. |
| **Globals threading** (how prior-agent outputs reach the next agent) | The `outputScript` on each Agent node in the workflow JSON | Edit the Scriban script. |
| **Mission text** (the immutable "what we're synthesizing") | Workflow input slot or a global initialized at the Start node | Edit the workflow input or the Start-node's setOutput script. |
| **Decision gates** (e.g. "≥10% lift over V1", "3× tokens") | This doc | Edit the doc. The runtime doesn't enforce these — they're decisions you and I make when reading the results. |

**The one code-bound piece** is the Swarm node *itself* — that's runtime work in sc-43. After it ships, even *its* knobs (N, mission, abstention behavior) are config, not code.

Practical implication: you can tune V1 and V2 freely — different N, different prompts, different models, different threading — and re-bench, without ever touching .NET. The bench harness is just "trigger trace → pull trace data → score output."

## Bench requests

Two requests in the v1 bench. Both are **synthesis-shaped** — the swarm thesis is multi-perspective combination, so a one-shot factual lookup doesn't exercise it. Both fit the candidate workflow shape (sc-45: *Intake → Expand requirements → Swarm → Review/QA*).

### Request A — PRD synthesis from a sparse brief

**Input:** A 3–5 sentence product brief describing a hypothetical CodeFlow capability ("a node that reads a Linear ticket and seeds a workflow with the description"). Deliberately under-specified: no acceptance criteria, no edge cases, no non-goals.

**Expected output:** A PRD-shaped document with sections for problem, users, goals, non-goals, acceptance criteria, open questions, dependencies. ~600–1500 words.

**Why this:** The paper's thesis predicts emergent role differentiation produces broader coverage than a single agent. PRD synthesis directly rewards that — different "roles" (PM, eng lead, QA, ops) surface different concerns.

**Scoring weight:** 1.0× (primary).

### Request B — Architecture tradeoff analysis

**Input:** A concrete forced choice — e.g., "We want workflow-level token budgets. Should we (a) enforce a hard ceiling at the saga level that aborts the trace when exceeded, (b) emit soft warnings per-node and let the workflow author decide whether to gate, or (c) do both — soft per-node warnings plus a saga-level hard ceiling? Recommend one with justification."

**Expected output:** A recommendation with explicit tradeoff matrix across at least: enforcement strictness, author ergonomics, failure-mode predictability, observability, implementation complexity. ~400–900 words.

**Why this:** Forces concrete reasoning over fixed dimensions; makes scoring more objective than open-ended PRD work.

**Scoring weight:** 1.0×.

> **Note:** Both inputs are deliberately CodeFlow-shaped so we can reuse them in sc-45 (the candidate real workflow). They also serve as smoke tests for the future dev/reviewer + dev/QA loops.

### Runs per variant per request

**N = 3 runs per (variant × request)** = **12 traces total** for the P1 bench (V1 + V2 × A + B × 3 runs) and **18 traces** for the P2 head-to-head (V1 + V2 + V3 × A + B × 3 runs). N=3 is enough to observe variance without burning budget; bump to 5 if variance dominates the variant effect.

## Quality rubric

5-point Likert across five dimensions, scored independently per dimension. Total = sum (5–25). Averaged across runs.

| Dimension | What it measures | 5 means | 1 means |
|---|---|---|---|
| **Coverage** | Did the output address every part of the request? | All aspects addressed; no obvious omissions | Major sections of the request unaddressed |
| **Coherence** | Internal consistency; no contradictions. | Tight, no contradictions, sections reinforce each other | Self-contradictory or fragmentary |
| **Specificity** | Concrete vs. generic. | Concrete file paths, numbers, named tradeoffs, examples | Generic platitudes, no commitments |
| **Actionability** | Could a reasonable engineer ship from this? | Ready to execute or refine; clear next step | Restating the problem; no path forward |
| **Perspective diversity** | Does the output reflect multiple framings/roles? *(swarm thesis)* | Clearly distinct perspectives or tradeoff axes surfaced | Single voice / single framing only |

**Two scorers per output, blinded:**
1. **Human (Michael)** — primary; resolves ties.
2. **LLM judge** — `claude-opus-4-7` with the rubric above and **blinded variant labels** (rename V1/V2/V3 to random A/B/C per request, shuffle order). Cross-check; flag any score disagreeing with human by ≥2 points for re-review.

**Why blinded labels:** The paper observes that knowing which protocol produced output biases scorers (there's a Cohen's d > 1 effect to defend). Blinded labels are cheap insurance.

**Optional self-abstention scorer (Phase 2+):** Track whether agents inside V2/V3 voluntarily abstained, and whether abstention correlates with quality. Out of scope for v1 rubric but capture in trace metadata.

## Capture mechanics — what the harness records per run

For each trace, capture the following into a results CSV (schema below). All fields read post-hoc from existing CodeFlow APIs — no new instrumentation required for v1.

### Token totals (the primary cost signal)
- **Source:** `GET /api/traces/{traceId}/token-usage` → `TraceTokenUsageDto`.
- **Fields recorded:**
  - `total_input_tokens` ← `Total.Totals["input_tokens"]`
  - `total_output_tokens` ← `Total.Totals["output_tokens"]`
  - `total_reasoning_tokens` ← `Total.Totals["output_tokens_details.reasoning_tokens"]` (0 if absent — sonnet-4-6 doesn't report)
  - `cached_input_tokens` ← `Total.Totals["input_tokens_details.cached_tokens"]` (0 if absent)
  - `call_count` ← `Total.CallCount`
  - `per_node_breakdown` ← `ByNode` (JSON column)
- **Per-node and per-scope rollups** are already computed by `TokenUsageAggregator` — surface them in the CSV's JSON sidecar columns.

> **Why no USD:** Tokens are the unit we care about. We are not computing $ cost — neither in the runtime nor in the harness. If we ever want a dollar figure, we can recompute post-hoc from the recorded token counts.

### Wall-clock latency
- **Overall:** `WorkflowSagaStateEntity.UpdatedAtUtc - CreatedAtUtc` → `wall_clock_ms`.
- **Per-node duration:** `decision.RecordedAtUtc - decision.NodeEnteredAtUtc`, where `NodeEnteredAtUtc` is the explicit dispatch timestamp landed by the prereq story *[P0+] Capture explicit per-node start timestamps in saga* (Shortcut, Epic 38). `WorkflowSagaDecisionEntity` carries both fields; one row per node round.
- **Per-LLM-call** (finer-grained, optional): `TokenUsageRecord.RecordedAtUtc` per round-trip within a node.
- **Why explicit start timestamps and not chained `RecordedAtUtc`:** chained timing breaks the moment any node runs in parallel (Phase 3 Coordinator), folds saga-dispatch overhead into `node[0]`, misattributes Subflow / ReviewLoop synthetic-decision durations, and forces every consumer (harness, timeline UI, future analytics) to re-derive. The prereq story is therefore a hard blocker for sc-41 — bench results depend on this field being present and trustworthy.

### Trace metadata
- `trace_id`, `workflow_id`, `workflow_version`, `variant` (`V1|V2|V3`), `request` (`A|B`), `run_index` (1..N), `model`, `started_at_utc`, `completed_at_utc`, `final_state` (`Done|Failed|Aborted`), `failure_reason` (if any).

## Results CSV schema

```csv
trace_id,variant,request,run_index,model,workflow_id,workflow_version,
started_at_utc,completed_at_utc,wall_clock_ms,
call_count,total_input_tokens,cached_input_tokens,total_output_tokens,total_reasoning_tokens,
final_state,failure_reason,
score_coverage,score_coherence,score_specificity,score_actionability,score_perspective,score_total,
scorer_human,scorer_llm,
per_node_json,per_scope_json
```

Stored at `bench/swarm/results/<run-date>.csv`. Inputs at `bench/swarm/requests/{A,B}.md`. Scoring sheets at `bench/swarm/scores/<run-date>-<scorer>.csv`.

> The `bench/` tree does not yet exist — sc-41 (P1 bench execution) is responsible for creating it. This doc just specifies the layout.

## Run protocol (for sc-41 and sc-44)

0. **Prereq:** [sc-80](https://app.shortcut.com/trefry/story/80) (explicit per-node start timestamps) is shipped — required for trustworthy per-node latency.
1. Confirm V1 + V2 (and V3 for sc-44) workflow library entries are imported and on the same model.
2. Author request A and request B as input markdown under `bench/swarm/requests/`.
3. For each `(variant, request)` pair, run N=3 traces. Record trace IDs.
4. Pull `/api/traces/{id}/token-usage` and saga timestamps for each trace; populate the CSV.
5. Strip variant labels, randomize order, hand to human scorer. Hand same set to LLM judge.
6. Reconcile scores; re-score any output where human and LLM disagree by ≥2 on any dimension.
7. Compute aggregates: mean/stdev of (score_total, wall_clock_ms, total_input_tokens, total_output_tokens) per variant per request.
8. Write `bench/swarm/results/<run-date>-summary.md` with the table + a paragraph of interpretation. Attach to the relevant story (sc-41 or sc-44) and the epic.

## Decision gates

- **After sc-41 (P1 bench):** If V2 (Sequential subflow) doesn't show ≥10% mean score lift over V1 *or* burns >3× the tokens without compensating quality, **stop before Phase 2.** Either the prompt pattern doesn't hold on our task shapes, or our requests don't reward emergence.
- **After sc-44 (P2 bench):** Compare V3 to V2. If V3 isn't materially better than V2 (the prompting pattern), it's an expensive runtime for the same outcome — flag for design review before pursuing Phase 3 (Coordinator).

## Follow-ups (not part of sc-39)

- **Bench tooling.** Right now this doc treats the harness as "scripts the human runs." If we run the bench enough times, fold the steps in §"Run protocol" into a CLI under `tools/swarm-bench/`. Premature for v1.

## Open questions deferred to later phases

These belong to sc-42 (P2 design slice), not P0:
- Agent-pool config: single base AgentRole self-specializing (paper-faithful) vs. curated role pool (CodeFlow-shaped).
- Self-abstention semantics: structured `{abstain, reason}` field vs. routing through implicit `Failed` port.
- Mission text: input port vs. node config.
- Trace shape for per-agent role + transcript: output port vs. ReviewLoop-style side-channel.

V2 (the hand-authored subflow in sc-40) makes a *concrete* choice for each — those choices feed sc-42's design with real data.

---

*This is the v1 design. Updates land in this file directly (not as `-v2.md`); the git history is the version trail.*
