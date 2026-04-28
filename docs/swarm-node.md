# Swarm Node — Design (P2)

**Epic:** [Self-Organizing Agents (Swarm Node) Exploration](https://app.shortcut.com/trefry/epic/38) (sc-38)
**Story:** [P2] Design Swarm node — [sc-42](https://app.shortcut.com/trefry/story/42)
**Status:** Design — locks the open questions before runtime work in [sc-43](https://app.shortcut.com/trefry/story/43) (Sequential implementation) and [sc-46](https://app.shortcut.com/trefry/story/46) (Coordinator implementation)
**Source paper:** *Self-Organizing LLM Agents* — https://arxiv.org/html/2603.28990v1
**Bench:** [`bench/swarm/results/2026-04-28-summary.md`](../bench/swarm/results/2026-04-28-summary.md) — V2 (hand-authored Sequential subflow) +9.4 % score lift over V1 at × 10 input tokens. Decision-gate verdict overridden; the runtime primitive proceeds for exploration value and downstream-amplification on document-shaped outputs.

This doc is the contract. Both Sequential and Coordinator land in v1; Broadcast and Shared remain Phase-4-deferred. Anything decided here is settled; sc-43 and sc-46 implement against it.

## Goals

1. Add `WorkflowNodeKind.Swarm` with two protocols (`Sequential`, `Coordinator`) author-selectable per node.
2. Reuse the existing saga decision ledger so per-agent contributions are visible in the trace inspector without new tables.
3. Composable with the rest of the runtime: same input-port semantics as Subflow, same Failed port semantics as every other node, same `mirrorOutputToWorkflowVar` / output-script surface as Agent nodes.
4. Forward-compatible with Phase-4 protocols (Broadcast, Shared) — same node kind, new enum values.
5. Marked **non-replayable** at the node-kind level: replay-with-edit re-executes a Swarm node fresh, never substitutes prior outputs.

## Non-goals (v1)

- HITL gates inside the swarm. HITL gates remain on input/output ports of the Swarm node only.
- Per-instance replayability flag. The whole node kind is non-replayable; no per-author override.
- Cost ceilings in USD. Tokens only — see [`feedback_no_dollar_cost_in_app.md`](https://github.com/anthropics/) memory.
- Hierarchical agent pools (per-team, per-org). Single configured pool per node.
- Streaming partial output mid-swarm. Synthesizer's terminal output is what flows downstream.
- Swarm-of-swarms with shared organizational memory. That's the Shared protocol (Phase 4).

## Node configuration

```json
{
  "id": "<guid>",
  "kind": "Swarm",
  "protocol": "Sequential",
  "n": 4,
  "contributorAgentKey": "swarm-contributor",
  "contributorAgentVersion": 1,
  "synthesizerAgentKey": "swarm-synthesizer",
  "synthesizerAgentVersion": 1,
  "coordinatorAgentKey": null,
  "coordinatorAgentVersion": null,
  "tokenBudget": null,
  "outputPorts": ["Synthesized"],
  "layoutX": 350,
  "layoutY": 200,
  "outputScript": null,
  "mirrorOutputToWorkflowVar": null
}
```

| Field | Type | Required | Notes |
|---|---|---|---|
| `protocol` | `"Sequential"` \| `"Coordinator"` | yes | Closed enum. Adding values is a runtime change. |
| `n` | int (1..16) | yes | Sequential: number of contributors. Coordinator: max workers (the coordinator may pick fewer). |
| `contributorAgentKey` / `contributorAgentVersion` | string + int | yes | Agent role used at every contributor position (and every Coordinator worker position). One role, reused; per-position differentiation comes from the assignment / priors fed into the prompt template, not from separate roles. |
| `synthesizerAgentKey` / `synthesizerAgentVersion` | string + int | yes | Agent role for the final synthesis step. Runs once after all contributors complete. |
| `coordinatorAgentKey` / `coordinatorAgentVersion` | string + int | only when `protocol = Coordinator` | Agent role that runs first in Coordinator mode and returns assignments. Validator rejects the workflow on save if `protocol = Coordinator` and these are null; rejects if `protocol = Sequential` and these are non-null. |
| `tokenBudget` | int? | no | Optional token cap on the cumulative swarm-internal LLM usage (input + output, summed across coordinator + workers + synthesizer). When exceeded, see [§"Token budget"](#token-budget). |
| `outputPorts` | `string[]` | yes | At least `["Synthesized"]`. Same as every node — implicit Failed exists and is unwirable. |
| `outputScript` / `mirrorOutputToWorkflowVar` | as on Agent nodes | no | Apply to the synthesizer's terminal output exactly the way they apply to any Agent node. |

The contributor / synthesizer / coordinator agents are **standard `AgentRole` definitions** — same `systemPrompt`, `promptTemplate`, `provider`, `model`, `outputs` fields as every other agent in the library. The Swarm node references them by key+version, identical to how an Agent node references its agent.

### Validator rules (save-time)

1. `protocol IN {Sequential, Coordinator}`.
2. `n` in `[1, 16]`. The upper bound is conservative for v1 — the paper sweeps to N=64 but our trace-inspector UI hasn't been pressure-tested at that fan-out. Bumping is a config-only change later.
3. `contributorAgentKey`, `contributorAgentVersion`, `synthesizerAgentKey`, `synthesizerAgentVersion` non-null and reference existing agents.
4. If `protocol = Coordinator`: `coordinatorAgentKey`, `coordinatorAgentVersion` non-null and reference existing agents. If `protocol = Sequential`: both must be null.
5. `tokenBudget` either null or `> 0`.
6. The contributor and synthesizer agents must declare an output port that the Swarm node consumes. Agents declare their port lists via their `outputs[]`; the Swarm runtime accepts the agent's first-listed port name as the contribution / synthesis terminal. Agents with multiple output ports are valid (the runtime ignores all but the first), but the editor surfaces a warning.

## Two protocols

```
Sequential                              Coordinator
══════════                              ═══════════

Mission flows in via input port         Mission flows in via input port
  ↓                                       ↓
R0 — contributor #1                     R0 — coordinator
  (sees: mission)                         (sees: mission, swarmMaxN)
  ↓                                       ↓ (returns N≤max assignments)
R1 — contributor #2                     R1..Rn — N workers in parallel
  (sees: mission, contribution[0])        (each sees: mission, its own assignment)
  ↓                                       ↓ (saga waits on PendingParallelRoundIds)
R2 — contributor #3                     Rn+1 — synthesizer
  (sees: mission, contributions[0..1])    (sees: mission, all N contributions)
  ↓                                       ↓
R3 — contributor #4                       Synthesized
  (sees: mission, contributions[0..2])
  ↓
R4 — synthesizer
  (sees: mission, all contributions)
  ↓
  Synthesized

Sequential: n+1 LLM calls               Coordinator: n+2 LLM calls
            n contributors + 1 synth                 1 coord + n workers + 1 synth
```

Both protocols terminate on the synthesizer's port. The synthesizer's output is the Swarm node's terminal artifact.

## Saga state changes

Two new fields on `WorkflowSagaStateEntity`:

| Field | Type | Purpose |
|---|---|---|
| `PendingParallelRoundIdsJson` | `string?` | Serialised list of `Guid` round IDs the saga is awaiting completions on. Empty / null in Sequential mode and outside Swarm-Coordinator dispatch. |
| `CurrentSwarmCoordinatorNodeId` | `Guid?` | The Swarm node ID those round IDs belong to. Used by the stale-round guard so a parallel completion arriving after a saga has moved past the Swarm node is rejected, not silently merged. |

Both are nullable to keep the column behaviour additive — pre-migration sagas and Sequential-only swarms see them as `null`.

### EF migration

Two columns on `workflow_sagas`. Both `datetime`/`varchar`-shaped, both nullable, no backfill required (in-flight sagas at deploy time can't be inside a Coordinator dispatch because the kind doesn't exist yet).

## Decision ledger / trace shape

Every contributor (and the coordinator and the synthesizer) writes a row to `WorkflowSagaDecisionEntity` at completion, just like Agent nodes do today. Reading the ledger top-to-bottom for one Swarm node yields:

**Sequential (n=4):** 5 rows — contributor #1, #2, #3, #4, synthesizer. All with `NodeId = swarmNode.Id`. RoundIds advance monotonically (each contributor rotates rounds — Sequential is non-rotating but every contributor counts as a fresh round for trace clarity). `NodeEnteredAtUtc` (sc-80) gives each its dispatch timestamp.

**Coordinator (n=4):** 6 rows — coordinator (R0), then 4 worker rows (R1..R4) with overlapping `NodeEnteredAtUtc`/`RecordedAtUtc` ranges, then synthesizer (R5). The trace timeline UI's existing per-node clustering renders the parallel rows correctly because each one has its own `NodeEnteredAtUtc`. (This is exactly the case sc-80 was forward-built for.)

The decision row's `Decision` field is the agent's effective port name. For contributors and synthesizers that's the agent's declared output port (`Contributed`, `Synthesized`, etc.). For an abstaining contributor it's still `Contributed` — abstention is captured in the artifact body, not the port (see [§"Abstention"](#abstention)).

### Per-agent role + transcript

The agent's role choice and full message body are already in the decision row's `OutputRef` (artifact URI) and message contents — surfacing them is a trace-inspector concern, not a saga concern. **No side-channel needed.** The trace inspector should read the artifact body for each contributor row and parse the `ROLE:` line for the role label; sc-43 includes a small UI affordance for this on the Swarm node's expanded view.

## Template variables

Each agent's Scriban prompt template can read these:

| Variable | Available to | Value |
|---|---|---|
| `workflow.swarmMission` | contributor, synthesizer, coordinator | The Swarm node's input artifact text. Set by the runtime at Swarm-node entry, before any contributor runs. Cleared at Swarm-node exit. |
| `workflow.swarmContributions` | contributor, synthesizer | Array of prior contribution objects (see schema below). Contributor at position `i` sees positions `[0..i-1]` (Sequential) or `[]` (Coordinator workers run in parallel and don't see each other's outputs). Synthesizer sees all. |
| `swarmPosition` | contributor | 1-indexed position in the chain (Sequential) or assignment slot (Coordinator). 1..n. |
| `swarmAssignment` | contributor | Coordinator mode only: the coordinator's specific assignment for this worker (a string — the role / sub-task). `null` in Sequential mode. |
| `swarmMaxN` | coordinator | The configured `n` cap. Coordinator may return fewer assignments. |
| `swarmEarlyTerminated` | synthesizer | `true` if the token budget triggered early termination before all contributors completed; `false` otherwise. Lets the synthesizer prompt branch on partial input. |

### `swarmContributions` schema

Each entry is an object:

```json
{
  "position": 1,                    // 1-indexed
  "role": "analyst",                // parsed from the contributor's first-line "ROLE: ..."; null if no ROLE: line
  "abstained": false,               // true if first line was "ROLE: abstain"
  "text": "<full contributor output>",
  "agentKey": "swarm-contributor",
  "agentVersion": 1
}
```

The `role` and `abstained` fields are runtime-parsed from the contributor's text — the runtime looks for a leading `ROLE: <value>` line on each contribution. This matches V2's hand-authored convention so library entries can migrate cleanly. Agents that don't emit a ROLE line get `role: null, abstained: false`.

### Why `swarmContributions` as an array, not per-position globals

V2's hand-authored library entry mirrored each contribution into separate workflow vars (`swarmContribution1`, `swarmContribution2`, ...). The runtime standardises on a single array because:

1. The Coordinator protocol's worker count is dynamic up to `n` — per-position globals don't fit.
2. The synthesizer prompt is simpler when iterating: `{{ for c in workflow.swarmContributions }}{{ c.text }}{{ end }}`.
3. Library entries that pre-date the runtime (V2) stay valid as-is — the runtime never *writes* to `swarmContribution1..n`; it writes to `swarmContributions`. V2 lives on as a hand-authored alternative.

## Abstention

A contributor signals abstention by making the first line of its output exactly `ROLE: abstain`, optionally followed (on the next line) by a one-sentence reason.

This is the convention V2 established and the bench validated. Reasons to keep it:

- **No structured-output coupling.** Contributors don't need to emit JSON; they emit prose. Authors can swap models freely.
- **Synthesizer can branch on `c.abstained`** in its template without parsing JSON.
- **Failed port is wrong here.** Failed means *error* (an invariant broke, the agent crashed, a tool refused). Abstention is a deliberate judgment call. Reusing Failed would conflate the two and break the existing "wire Failed for recovery" convention.

The runtime's parsing is deliberately strict: only an exact `ROLE: abstain` opener marks `abstained: true`. Anything else (`ROLE: abstaining`, `Abstaining:`, etc.) is treated as a regular contribution. This keeps the parser predictable and shifts the burden onto the contributor's prompt template — V2's contributor prompt already enforces the format.

If the **synthesizer** also wants to abstain, that's a Failed-port case (the swarm produced no usable answer) — implementer choice in sc-43 whether the synthesizer's `ROLE: abstain` opener routes to Failed or just emits an "I have nothing to add" Synthesized output.

## Token budget

Optional `tokenBudget` (int, in tokens; null = unlimited).

The saga tracks cumulative token usage *for this Swarm node only* — sum of `input_tokens + output_tokens` across the coordinator (if any) + completed contributors + synthesizer (if reached). Subflow / ReviewLoop iterations *inside* the swarm (which there shouldn't be in v1, but the brief allows nesting) roll up.

**Behaviour on exceed:**

1. **In progress, contributors not all dispatched yet (Sequential)**: stop dispatching new contributors. Run the synthesizer immediately with whatever contributions are in `swarmContributions` so far. Set `swarmEarlyTerminated = true` so the synthesizer's prompt can flag the partial result.
2. **In progress, coordinator workers in flight (Coordinator)**: do not abort in-flight workers (they're already burning tokens; aborting wastes the spend). Wait for them to settle. Skip dispatching the synthesizer if the budget is already blown by ≥10 % at that point — instead, emit a `Failed` port termination with reason `"Swarm token budget exceeded by N% before synthesizer dispatch."`. Otherwise run the synthesizer with `swarmEarlyTerminated = true`.
3. **Already at synthesizer**: let it run. The budget is advisory at the synthesizer step — by then most of the cost is sunk and the value of the answer comes from the synthesis itself.

The synthesizer's prompt template is responsible for handling `swarmEarlyTerminated = true` gracefully (e.g., explicitly note the partial input in its output). The library's default synthesizer prompt should include that branch.

If `tokenBudget = null`, none of this applies — the swarm runs unbounded.

## Mission input

**Mission flows via the input artifact**, not via node config. Same as every other node. The Swarm node's `in` port receives whatever upstream emitted; the runtime treats that artifact's text body as `workflow.swarmMission`.

Reasons:

- **CodeFlow precedent.** Subflow, ReviewLoop, Agent nodes all consume input via the input artifact. Swarm fits in.
- **Reuse.** A single workflow can drive the Swarm node from many different upstream paths (fork on a Logic node, alternate from a HITL form, etc.) without re-authoring the node.
- **Replay-with-Edit.** Even though the Swarm node itself is non-replayable, replay can swap the input artifact at the Swarm node's input port — a common operator pattern from the bench.

The paper treats mission as immutable per run, but that property is preserved by the saga pattern (the saga snapshots its input at dispatch). Author-time vs. runtime is the difference; CodeFlow does runtime.

## Replayability

`WorkflowNodeKind.Swarm` is **non-replayable at the type level**. No per-instance flag.

Concretely: when the operator triggers a Replay-with-Edit on a trace that contains a Swarm node, the replay path executes the Swarm node fresh — the prior trace's contributor / synthesizer outputs are NOT substituted in. This matches the runtime semantics already used for trace replay through nodes that can't be reproduced deterministically.

The replay engine (`DryRunExecutor` + the replay endpoint from sc-T2) needs one new check: if the node being executed is a Swarm node, never use the substitution path.

## Implementation slicing

This design lands in two stories:

### sc-43 — Swarm node (Sequential only)

- New `WorkflowNodeKind.Swarm` enum value + node-record fields (`protocol`, `n`, `contributorAgentKey`/`Version`, `synthesizerAgentKey`/`Version`, `coordinatorAgentKey`/`Version`, `tokenBudget`).
- Save-time validators per [§"Validator rules"](#validator-rules-save-time).
- `WorkflowSagaStateEntity` migration adding `PendingParallelRoundIdsJson` and `CurrentSwarmCoordinatorNodeId`. Both nullable; sc-43 only writes them in Sequential's degenerate "always empty" form, but the schema lands here so sc-46 doesn't need its own migration.
- State machine: dispatch contributor #1 on Swarm-node entry; on each `AgentInvocationCompleted` for a Swarm-contributor round, append the row, advance position; when position > n, dispatch synthesizer. On synthesizer completion, route on its port.
- Token-budget check after each contributor's completion (only the `Sequential` paths in [§"Token budget"](#token-budget) apply).
- Template-variable wiring: runtime sets `workflow.swarmMission`, `workflow.swarmContributions`, `swarmPosition`, `swarmEarlyTerminated`. `swarmAssignment` and `swarmMaxN` always null in Sequential.
- ROLE-line parsing for abstention.
- Replay-engine guard.
- Tests: end-to-end with a 4-contributor + 1-synthesizer swarm on a fake-LLM harness. Cover happy path, abstention, token-budget early termination.

### sc-46 — Coordinator protocol

Adds parallel execution **on top of the Sequential implementation** — no rewrite, additive code paths.

- `WorkflowSagaStateEntity` columns from sc-43 are now actively used.
- Coordinator dispatch path: run coordinator (single round), parse its assignment payload, generate N round IDs, persist into `PendingParallelRoundIdsJson`, set `CurrentSwarmCoordinatorNodeId`, publish N `AgentInvokeRequested` messages.
- Stale-round guard extended: accept completions on `RoundId IN PendingParallelRoundIds`.
- Pending-set drain detection → synthesizer dispatch.
- Token-budget check covers the Coordinator-specific cases in [§"Token budget"](#token-budget).
- Template variables: `swarmAssignment` populated from coordinator's payload; `swarmMaxN` populated from node config.
- Tests: parallel execution with N=4 workers; verify out-of-order completions are accepted; budget-exceeded mid-flight scenario.

### Shared infrastructure (lands in sc-43, used by both)

- Decision-ledger writing: identical for both protocols (sc-43 builds it; sc-46 just adds rows from parallel completions).
- Trace-inspector affordance for the Swarm node's expanded view (per-contributor role label, abstention chip, position number). May land in sc-43 with Sequential-only; sc-46 verifies it scales to parallel.

## Open questions deferred to implementation

These are decided by sc-43 / sc-46 with the design above as a starting point — small enough that the design doc shouldn't lock them prematurely:

1. **`n` cap upper bound.** Set to 16 here. If sc-43's UI testing surfaces issues at n=8 already, drop to 8 and revisit later. If everything's fine at 16, the bound stays.
2. **Coordinator's assignment-payload format.** The doc above implies "JSON list of strings" but doesn't lock the exact schema. sc-46 picks the cleanest form — likely `[{role: string, subTask: string}]` so contributors get both a role label and a sub-task brief. The contributor's `swarmAssignment` is then the matching object.
3. **Exact `ROLE: abstain` parser strictness.** Case-sensitive? Tolerate trailing whitespace? sc-43 picks; document in the runtime tests.
4. **Synthesizer-as-abstainer behavior.** Failed port vs. minimal Synthesized output. sc-43 picks based on which is simpler to author against.
5. **Trace-inspector's exact rendering of parallel rows.** sc-46 (or a follow-up UI ticket) picks layout — vertical lanes, horizontal grouping, etc. Decision-ledger data is sufficient for any layout.

## References

- Source paper: https://arxiv.org/html/2603.28990v1
- Harness doc: [`docs/swarm-bench-harness.md`](swarm-bench-harness.md)
- P1 bench result: [`bench/swarm/results/2026-04-28-summary.md`](../bench/swarm/results/2026-04-28-summary.md)
- V2 hand-authored library entry: [`workflows/swarm-bench-sequential-v1-package.json`](../workflows/swarm-bench-sequential-v1-package.json)
- Subflow design (precedent for parent/child saga interaction): [`docs/subflows.md`](subflows.md)
- ReviewLoop design (precedent for bounded iteration in a single node): [`docs/review-loop.md`](review-loop.md)
- Port model (terminal-port semantics): [`docs/port-model.md`](port-model.md)
