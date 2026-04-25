# Review Loop Node

> Requirements LOCKED 2026-04-23 after three review passes. Shipped 2026-04-23.
>
> The implementation-plan slices (§4) and "Sequencing notes" (§5) are preserved as the original design — current behaviour matches §2.1–§2.9. Decision-routing semantics changed in 2026-04 with the port-model redesign: port names are author-defined strings (no enum); see [port-model.md](port-model.md) for the canonical rules. Older mentions of `AgentDecisionKind` / `Approved` / `Rejected` / `Escalated` in the implementation-plan body refer to the original enum-based design.
>
> Companion references: [port-model.md](port-model.md), [subflows.md](subflows.md), [workflows.md](workflows.md).

## 1. Goal

Give workflow authors a **specialized subflow** that expresses a bounded produce-review-revise loop without leaking loop plumbing into the workflow editor. The same shape is reusable across requirements drafting, code generation, QA scenarios — anything that iterates "draft → critique → revise."

Today the primitive is cyclic edges flagged with `RotatesRound`, but:

- `RoundCount` resets on every rotation, so `MaxRoundsPerRound` never caps a review loop.
- Neither producer nor reviewer can tell which round they are on, so the reviewer will nit-pick forever.
- Authoring requires understanding cyclic edges + `RotatesRound` + counters — implementation trivia leaking into the builder.

The `ReviewLoop` node is a Subflow that:

1. Re-invokes the referenced child workflow up to `MaxRounds` times.
2. Feeds each round's terminal output artifact back as the next round's input.
3. Exposes `{{round}}` / `{{maxRounds}}` / `{{isLastRound}}` to the child's agents, prompts, templates, and scripts.
4. When the child terminates, compares the terminal port name to the configured `loopDecision`: equal → iterate; otherwise → exit on that port name. Round budget reached without an iterate-match → exit via the synthesized `Exhausted` port. See [port-model.md](port-model.md).

Everything about the child — agents, prompts, logic nodes, scripts, sub-subflows — is authored exactly like any other workflow. The ReviewLoop only adds iteration, counters, and the outcome mapping.

## 2. Concepts

### 2.1 Relation to Subflow

A `ReviewLoop` **is a subflow** with two behavioural additions:

| Subflow today | ReviewLoop |
|---|---|
| Child runs once | Child runs 1..N times (N = `MaxRounds`) |
| Output ports inherited from child terminal-port set + implicit `Failed` | Output ports = child terminal-port set ∪ `{Exhausted}` minus `loopDecision`, + implicit `Failed` |
| Child's terminal port name routes to a parent edge `fromPort` of the same name | Child's terminal port name drives loop control (matches `loopDecision` → iterate) + becomes the exit port name |

All other subflow behaviour carries over unchanged:

- Child workflow reference: `SubflowKey` + `SubflowVersion` (same pinning semantics).
- Two-tier context: each round's child saga has its own fresh local `context`; `global` is snapshotted at node entry, carried across rounds, shallow-merged back on loop exit.
- Counts toward `MaxSubflowDepth = 3` exactly like a plain Subflow.
- Version pinning + save-time resolution identical.

### 2.2 Iteration and feedback

- **Round 1 input** = the `ReviewLoop` node's input artifact (the artifact routed into the node by its predecessor).
- **Round N+1 input** (when the child exited "please revise") = **Round N's terminal output artifact**. The workflow author decides what that artifact contains — typically the revised artifact plus the reviewer's feedback, shaped however the next round's agents expect to consume it. No auto-wrapping / no envelope.
- Each round is a fresh child saga — new `TraceId`, fresh local `context` — so scripts don't accidentally carry state between rounds. The author opts into state-carrying by writing to `global`.
- `global` is shallow-merged between rounds (round N's writes visible to round N+1) and merged back into the parent only when the loop exits.
- Per-round artifacts are captured for free: each round is its own child saga with its own `TraceId`, so existing saga-history persistence records every round's inputs and outputs.

### 2.3 Outcome mapping

The ReviewLoop inspects the child saga's terminal port name (the port the child saga walked
off the graph on, after any routing-script overrides). The configured `LoopDecision` (default
`"Rejected"`) is the iterate signal: when the terminal port equals `LoopDecision`, the
ReviewLoop spawns the next round. Anything else exits on the matching parent port.

| Child terminal port name | Rounds remaining | Outcome |
|---|---|---|
| Equals `LoopDecision` (default `"Rejected"`) | > 0 | Advance to next round with this round's output as input |
| Equals `LoopDecision` | 0 (last round just ran) | Exit via the synthesized **`Exhausted`** port |
| `Failed` (FailTool, exception, or MaxSubflowDepth exceeded) | any | Exit via the implicit **`Failed`** port |
| Any other declared port name | any | Exit via the parent edge with the same `fromPort` name |

### 2.4 Ports

A ReviewLoop node's port set is derived (not author-edited):

- **Inherited** from the pinned child workflow's terminal-port set, **minus** the configured
  `LoopDecision` port (because that one means "iterate, don't exit").
- Plus the synthesized **`Exhausted`** port (round budget reached without exit).
- Plus the implicit **`Failed`** port (always wirable on every node).

### 2.5 Node configuration

Stored on the workflow node, edited in the inspector panel:

| Field | Type | Notes |
|---|---|---|
| `SubflowKey` | `string` | Required. Child workflow key. Same semantics as Subflow. |
| `SubflowVersion` | `int?` | `null` = latest at workflow save time. Same pinning as Subflow. |
| `MaxRounds` | `int` | Required. `[1, 10]`; default 3. |

The child workflow owns prompt authoring and uses `{{isLastRound}}` to shape its own prompts for the final pass.

### 2.6 Template variables + script bindings exposed to the child

In addition to the existing `context.*` and `global.*` bindings available inside any subflow, a child saga spawned by a `ReviewLoop` node gains:

| Name | Type | Scope |
|---|---|---|
| `round` | `int` (1-indexed) | Template var `{{round}}`; Jint binding `round` in logic scripts. |
| `maxRounds` | `int` | Template var `{{maxRounds}}`; Jint binding `maxRounds`. |
| `isLastRound` | `bool` | Template var `{{isLastRound}}`; Jint binding `isLastRound`. |

A child saga not spawned by a ReviewLoop does not see these bindings. For consistency with scripts that might be shared between ReviewLoop and non-ReviewLoop callers, absent values read as sentinel defaults (`round = 0`, `maxRounds = 0`, `isLastRound = false`).

### 2.7 Depth and nesting

- A ReviewLoop node at depth `D` spawns child sagas at depth `D+1`, same as Subflow.
- All rounds of a single ReviewLoop run at the same depth (`D+1`); iterating does not accumulate depth.
- A ReviewLoop inside a Subflow inside a ReviewLoop is legal as long as nesting depth ≤ 3.
- `SubflowDepthExceeded` on any round's spawn → loop exit `Failed`.

### 2.8 HITL

The child workflow can contain HITL nodes exactly as any subflow can. Pending HITL inside any round surfaces on enclosing traces' `pendingHitl` list using the existing aggregation rule ([subflows.md §2.5](subflows.md#25-hitl-surfacing)). Since rounds are sequential, at most one round is active at a time, so the HITL aggregation doesn't need to change.

A HITL reviewer for the whole loop goes **outside** the loop in the parent workflow (wired to the `Approved` / `Exhausted` / `Failed` ports). HITL inside a round is not special-cased.

### 2.9 Tracing

- Each round shows in the parent trace as a child trace, same as a Subflow invocation.
- The ReviewLoop node row in the parent trace shows `Round N of M` progress and the current round's child trace link.
- After the loop exits, the trace detail UI renders the ReviewLoop node with expandable per-round child traces underneath.

### 2.10 Historical note: enum removals

This section is historical. `ApprovedWithActions` was removed from `AgentDecisionKind` in
2026-04 (its own epic), and `AgentDecisionKind` itself was deleted as part of the port-model
redesign — port names are now author-defined strings, not enum values. ReviewLoop's iterate
signal is the configured `LoopDecision` (default `"Rejected"`); any other terminal port name
from the child exits the loop on a parent edge of the same name. See
[port-model.md](port-model.md) for the canonical rules.

## 3. Out of scope (v1)

- HITL reviewer as a first-class node-level configuration. Put HITL outside the loop in the parent workflow or inside the child workflow.
- Structured, machine-typed reviewer output. The child workflow decides what its output artifact contains.
- Round-conditional child workflow versions (e.g. "use workflow A on rounds 1–2, workflow B on round 3"). Single child workflow per ReviewLoop node.
- Retry-on-failure inside the loop — any failed round exits `Failed`.
- Round-over-round history arrays exposed to the child. Only the previous round's output artifact flows in; the child can persist anything else it wants via `global`.
- Parallel rounds / quorum reviewers.
- Mid-run mutation of `MaxRounds`.

## 4. Implementation plan — slices

Each slice is sized for one Kanban card. (Historical: the `ApprovedWithActions` removal lived in its own epic and shipped before this work; `AgentDecisionKind` itself was later removed by the port-model redesign.)

### Slice 1 — Persistence: schema for ReviewLoop node

- Add `WorkflowNodeKind.ReviewLoop = 6` to [WorkflowNodeKind.cs](../CodeFlow.Persistence/WorkflowNodeKind.cs).
- Reuse existing `workflow_nodes.subflow_key` / `subflow_version` columns (same references as Subflow).
- Add `review_max_rounds INT NULL` to `workflow_nodes`; mirror on `WorkflowNodeEntity` / `WorkflowNode`.
- Add to `workflow_sagas`:
  - `parent_review_round INT NULL` — 1-indexed round number for child sagas spawned by a ReviewLoop; NULL for plain subflow / top-level sagas.
  - `parent_review_max_rounds INT NULL` — the configured `MaxRounds` snapshot so the child can compute `isLastRound` without re-reading the parent node.
- Single EF migration.
- Persistence round-trip test.

### Slice 2 — Contracts: ReviewLoop round-aware subflow messages

- Extend `SubflowInvokeRequested` with optional `ReviewRound` and `ReviewMaxRounds` fields, populated only when the parent node is a `ReviewLoop`. Existing Subflow invocations leave them null.
- `SubflowCompleted` carries the child's terminal `AgentDecisionKind` so the parent saga can drive ReviewLoop mapping without re-fetching saga state.
- Contract-level tests for serialization with and without ReviewLoop fields.

### Slice 3 — Saga: ReviewLoop dispatch + round iteration

- Extend `DispatchToNodeAsync` in [WorkflowSagaStateMachine.cs](../CodeFlow.Orchestration/WorkflowSagaStateMachine.cs) so `WorkflowNodeKind.ReviewLoop` spawns a child saga the same way Subflow does, but with `ReviewRound = 1` and `ReviewMaxRounds = node.MaxRounds`.
- On `SubflowCompleted` from a child saga whose parent node is a `ReviewLoop`, apply the §2.3 mapping:
  - `Approved` / `Completed` → route from `Approved` port with the round's output.
  - `Rejected` with rounds remaining → spawn a new child saga with `ReviewRound = prevRound + 1`, input = prev round's output, and the carried-forward `global` snapshot.
  - `Rejected` on the last round → route from `Exhausted` port.
  - `Failed` / `Escalated` → route from `Failed` port.
- Depth cap check runs on each round's spawn (reuses the existing Subflow depth-cap path).

### Slice 4 — Saga: global merge across rounds

- Between rounds, the previous child's final `global` is shallow-merged into the ReviewLoop's carried `global` before spawning the next round. Writes from round N are visible to round N+1 via `global.*`.
- On loop exit (Approved / Exhausted / Failed), the carried `global` is shallow-merged into the parent saga's `global` using the existing `SubflowCompleted` merge path.
- Unit test: a child workflow that does `setGlobal('counter', round)` across rounds is observed correctly at parent resume.

### Slice 5 — Scripting + templating: round variables

- Extend `BuildContextTemplateVariables` in [AgentInvocationConsumer.cs](../CodeFlow.Orchestration/AgentInvocationConsumer.cs) to expose `round`, `maxRounds`, `isLastRound` whenever the invoking saga has `parent_review_round` set.
- Extend [LogicNodeScriptHost.cs](../CodeFlow.Orchestration/Scripting/LogicNodeScriptHost.cs) with the same bindings for logic scripts.
- Out-of-ReviewLoop behaviour: bindings evaluate to sentinel defaults (`round = 0`, `maxRounds = 0`, `isLastRound = false`).

### Slice 6 — Save-time validation

- `WorkflowsEndpoints` save-time validation for `ReviewLoop` nodes:
  - `SubflowKey` exists; pinned `SubflowVersion` exists.
  - `MaxRounds` ∈ `[1, 10]`.
  - Reject self-reference (a workflow whose ReviewLoop points to itself) as a save-time quality-of-life check (runtime depth cap catches it too).
  - Reuse Subflow version resolution (`null` → latest at save).
- Unit tests for each branch.

### Slice 7 — Editor UI: ReviewLoop node

- Add ReviewLoop as a node type in [workflow-node-schemes.ts](../codeflow-ui/src/app/pages/workflows/editor/workflow-node-schemes.ts) and the canvas.
- Inspector controls: workflow picker (same component as Subflow), version selector, MaxRounds spinner.
- Three output handles: `Approved`, `Exhausted`, `Failed`.

### Slice 8 — Trace UI: ReviewLoop with per-round children

- [trace-detail.component.ts](../codeflow-ui/src/app/pages/traces/trace-detail.component.ts): ReviewLoop node row shows `Round N of M` and renders the current/completed child traces as collapsible children (same component as Subflow child trace display).
- Badge on rounds where the child's terminal decision was `Rejected` (so authors can see which rounds looped).

### Slice 9 — Documentation + sample

- Update [workflows.md](workflows.md) with the ReviewLoop node: shape, ports, `MaxRounds`, the feedback loop rule, the new template variables, context scoping.
- Add a short worked example: a "draft → critique → revise" loop with `MaxRounds = 3`, showing the child workflow that implements produce-then-review using `{{isLastRound}}` in the reviewer prompt.

### Slice 10 — End-to-end test

Integration tests covering:

1. Child approves on round 1 → parent exits `Approved` with round-1 output.
2. Child returns `Completed` on round 1 → parent exits `Approved` (permissive mapping).
3. Child revises on round 1, approves on round 2 → parent exits `Approved` with round-2 output.
4. Child revises on all rounds with `MaxRounds = 2` → parent exits `Exhausted` with round-2 output.
5. Child returns `Failed` on round 2 → parent exits `Failed`; any `global` writes from round 1 still merged back.
6. Child returns `Escalated` on round 1 → parent exits `Failed`.
7. Child workflow that reads `{{round}}` and `{{isLastRound}}` verifies the values per round.
8. ReviewLoop nested inside another ReviewLoop exceeds depth cap → `Failed` via `SubflowDepthExceeded`.

## 5. Sequencing notes

- (Historical) The `ApprovedWithActions`-removal epic shipped before Slice 2 of this epic. The port-model redesign later removed `AgentDecisionKind` entirely, so the saga mapping table in §2.3 is now described in terms of port-name strings.
- Slices 1–2 (schema + contracts) land together, no behaviour shipped.
- Slice 3 depends on 1+2; heaviest backend slice.
- Slices 4 and 5 can land in parallel after Slice 3.
- Slice 6 (validation) can land alongside Slice 3.
- Slices 7 and 8 (UI) don't block backend rollout.
- Slice 10 (E2E) after 3+4+5 merge.

## 6. Follow-ups (post-v1)

- Per-round child workflow overrides.
- Structured, machine-typed reviewer output passed forward as a first-class object.
- Retry-on-failure inside the loop.
- Quorum / multi-reviewer modes.
- Round-over-round history arrays exposed to the child.
- Visual cycle detection at save time (same call as Subflow v1).
