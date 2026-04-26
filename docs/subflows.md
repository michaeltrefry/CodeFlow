# Subworkflows (Workflow Composition)

> Requirements locked 2026-04-23. Implementation slices S1–S7, S10, S11 shipped; S8 (trace UI), S9 (editor UI), and S12 (end-to-end test) remain. Tracked as the "Subworkflow Composition" epic in Kanban.
>
> For the user-facing walkthrough and examples, see [workflows.md § Subflows (composing workflows)](workflows.md#subflows-composing-workflows). This file is the design reference.

## 1. Goal

Allow a workflow to embed another workflow as a single **Subflow** node so that small, reusable workflows can be composed into larger ones without copy-paste. The composing parent treats the subflow like any other agent-style node: one input artifact in, one output artifact + decision out.

## 2. Concepts

### 2.1 Two-tier context (`context` + `workflow`)

Two distinct objects are exposed to Logic node scripts and template variables:

| Name      | Scope                              | Lifetime                                 | Writable from inside?                          |
|-----------|------------------------------------|------------------------------------------|------------------------------------------------|
| `context` | The current workflow's local state | Per saga (top-level or subflow)          | Yes (`setContext`)                             |
| `workflow`  | Shared context, propagated up/down | Lives on the top-level saga              | Yes (`setWorkflow`) — writes bubble on completion |

- For a top-level workflow, `context` is the workflow's input bag (current behaviour, unchanged) and `workflow` starts empty (or with whatever initial values the API caller supplies).
- For a subflow, `workflow` is the parent's `workflow` snapshotted at the moment the Subflow node fires; `context` starts empty and is local to the child saga.
- `setContext('foo', x)` writes to **local** `context`. Local context never bubbles back to the parent.
- `setWorkflow('foo', x)` writes to the **child's working copy** of `workflow`. When the child saga completes, its final `workflow` is **shallow-merged** (last-write-wins per key) into the parent's `workflow` as part of `SubflowCompleted`. Effects only become visible to the parent on completion — there is no live shared store.
- `workflow` is name-safe in Jint — not a reserved word, not bound by the sandbox.

### 2.2 Subflow node

A workflow node of kind `Subflow` references another workflow:

- `SubflowKey: string` — the child workflow key.
- `SubflowVersion: int?` — pinned version, or `null` meaning "latest at parent-save time".
- Output ports: inherited from the pinned child workflow's terminal-port set (the union of unwired declared ports across the child's nodes), plus the implicit `Failed` port. See [port-model.md](port-model.md).

Version pinning behaves identically to agent versions on a node:
- `null` is resolved to the current latest at parent-workflow save and rewritten on the node, so a saved parent workflow version is reproducible.
- Re-saving the parent (creating a new workflow version) re-resolves `null` slots to the then-current latest.
- Explicit version pins are preserved across saves.

### 2.3 Child saga

When the parent saga reaches a Subflow node, it publishes `SubflowInvokeRequested`, which spawns a **child saga** with:

- A fresh `TraceId`.
- Parent linkage: `ParentTraceId`, `ParentNodeId`, `ParentRoundId`.
- A `SubflowDepth` (0 for top-level workflows; +1 per nested subflow).
- The parent's `workflow` snapshot stored as the child's working `workflow`; the child's `context` starts empty.
- Its own rounds, its own `MaxRoundsPerRound`, its own escalation node (if defined).

When the child saga reaches a terminal state, it publishes `SubflowCompleted`, which the parent saga consumes as if it were an `AgentInvocationCompleted` for the Subflow node. The child's last output artifact becomes the input to whatever the parent routes to next, and the child's final `workflow` is shallow-merged into the parent's `workflow` before routing.

### 2.4 Recursion guard

Maximum subflow depth is **3** levels under the top-level workflow (top-level = 0, deepest legal child = 3). Exceeding it fails the offending child immediately with reason `SubflowDepthExceeded` and bubbles `Failed` up the chain.

### 2.5 HITL surfacing

Pending HITL tasks anywhere in a trace's subtree are surfaced on every ancestor trace's `pendingHitl` list, each tagged with the owning trace and subflow key/path. HITL answering uses the existing global-by-task-id endpoints; no new endpoint needed for the answer side.

## 3. Out of scope (v1)

- Live shared state — `setWorkflow` only propagates on subflow completion, not mid-run.
- Streaming or partial outputs from child to parent.
- Subflow-specific access control.
- Static cycle detection at workflow save time (runtime depth cap is sufficient).
- Embedded sub-trace timeline rendered inside the parent trace UI.
- Inline HITL answering UI on the parent trace (deep link to the owning trace is enough).
- ~~Custom named output ports declared by a subflow~~ — **delivered by the port-model redesign (2026-04):** Subflow node port sets are inherited from the child's terminal-port set, so any author-declared port name on a child terminal node propagates to the parent verbatim. See [port-model.md](port-model.md).

## 4. Implementation plan — slices

Each slice is sized to be one Kanban card. Where a slice could plausibly be split, sub-bullets call out the breakdown.

### Slice 1 — Persistence: schema for Subflow node + saga linkage
- Add `WorkflowNodeKind.Subflow = 5` to [WorkflowNodeKind.cs](../CodeFlow.Persistence/WorkflowNodeKind.cs).
- Add `SubflowKey` (`VARCHAR(128)`) and `SubflowVersion` (`INT`, nullable) to `workflow_nodes` and `WorkflowNodeEntity`/`WorkflowNode`.
- Add `ParentTraceId`, `ParentNodeId`, `ParentRoundId` (all nullable `BINARY(16)`), `SubflowDepth` (`INT NOT NULL DEFAULT 0`), and `GlobalInputsJson` (`LONGTEXT NULL`) to `workflow_sagas` and `WorkflowSagaStateEntity`. (`InputsJson` keeps today's local-context semantics — see Slice 6.)
- Single EF migration covers both tables.
- Repository read/write paths updated; round-trip through `IWorkflowRepository` exercised by a new persistence test.

### Slice 2 — Contracts: subflow message types
- Add `SubflowInvokeRequested(parentTraceId, parentNodeId, parentRoundId, childTraceId, subflowKey, subflowVersion, inputRef, sharedContext, depth)` to `CodeFlow.Contracts`.
- Add `SubflowCompleted(parentTraceId, parentNodeId, parentRoundId, childTraceId, decision, outputRef)` to `CodeFlow.Contracts`.
- Contract-level tests for serialization and required-field validation.

### Slice 3 — Saga: spawn child on Subflow node
- Extend `DispatchToNodeAsync` in [WorkflowSagaStateMachine.cs](../CodeFlow.Orchestration/WorkflowSagaStateMachine.cs) so `WorkflowNodeKind.Subflow` publishes `SubflowInvokeRequested` instead of `AgentInvokeRequested`.
- Resolve `SubflowVersion = null` at parent-workflow save time (slice 9) — at saga time it must already be pinned, so this slice asserts non-null and fails fast otherwise.
- Snapshot the parent's `InputsJson` into `SharedContext` on the message.
- Set `Depth = parent.SubflowDepth + 1`. If exceeds 3, do **not** spawn — emit a synthetic failure as if the child returned `Failed` with reason `SubflowDepthExceeded`.

### Slice 4 — Saga: child initialization
- New consumer `SubflowInvokeRequestedConsumer` (or extend `WorkflowSagaStateMachine` initial event handling) that:
  - Creates the child saga with `TraceId = childTraceId` and parent linkage populated.
  - Stores the parent's `workflow` snapshot as the child's `global_inputs_json`; child's `inputs_json` (local `context`) starts empty.
  - Routes from the child workflow's Start node using the existing pipeline.

### Slice 5 — Saga: child completion → parent resume
- When a child saga reaches a terminal state (any unwired declared port, or `Failed`) AND has parent linkage, publish `SubflowCompleted` carrying the child's final `workflow` (shallow JSON object) and the terminal port name.
- New event/consumer on the parent saga that:
  - Shallow-merges the child's final `workflow` into the parent's `workflow` (last-write-wins per top-level key) before routing.
  - Maps `SubflowCompleted` to an `AgentInvocationCompleted`-equivalent and routes from the Subflow node's matching port.
- Use the existing `RouteCompletionAsync` flow so script ports / logic chains on the Subflow node behave the same as on Agent nodes.

### Slice 6 — Scripting: add `workflow` alongside `context`, with `setWorkflow`
- Update [LogicNodeScriptHost.cs](../CodeFlow.Orchestration/Scripting/LogicNodeScriptHost.cs) to bind both `context` (local) and `workflow` (working copy of the shared snapshot).
- `setContext` keeps writing to local; new `setWorkflow('foo', x)` writes to the working `workflow`. Both writes are captured per-evaluation and applied to the saga atomically alongside other context updates.
- For top-level workflows, `workflow` is bound to whatever the top-level saga stores (empty by default); existing scripts continue to read `context.foo` unchanged.
- Persist split on `workflow_sagas`: keep `inputs_json` as the **local** context (matches today's semantics), add `global_inputs_json LONGTEXT NULL` for the shared bag. Migration leaves the new column NULL on existing rows; saga read code treats NULL as `{}`.
- Update template variable population (`BuildContextTemplateVariables` in [AgentInvocationConsumer.cs](../CodeFlow.Orchestration/AgentInvocationConsumer.cs)) to expose both `context.*` and `workflow.*` to agent prompts.

### Slice 7 — HITL: surface descendants on parent trace
- Trace API (`/api/traces/{id}`) aggregates `pendingHitl` from the entire subtree by recursive walk on `parent_trace_id` linkage.
- Each entry is decorated with `originTraceId` and `subflowPath` (e.g. `["A", "B"]`) so the UI can label them.
- Bound the walk by `SubflowDepth` (which is at most 3) — no risk of unbounded recursion.

### Slice 8 — Trace UI: descendants HITL display + child link
- [trace-detail.component.ts](../codeflow-ui/src/app/pages/traces/trace-detail.component.ts): pending HITL section groups by origin trace; each group has a deep link to the owning trace.
- [traces-list.component.ts](../codeflow-ui/src/app/pages/traces/traces-list.component.ts): "Hide subflow children" toggle (default: show); badge on rows whose saga has a non-null parent trace.

### Slice 9 — Editor UI: Subflow node + version pinning
- Add Subflow as a node type in [workflow-node-schemes.ts](../codeflow-ui/src/app/pages/workflows/editor/workflow-node-schemes.ts) and the canvas component.
- Inspector controls:
  - Workflow picker (autocomplete by key).
  - Version selector with "Latest at save" sentinel; explicit pins persist.
  - Read-only outline of the chosen workflow.
- Save path resolves `null` versions to current latest, identically to how agent latest-version pinning currently works.

### Slice 10 — Workflow validation: Subflow nodes
- Save-time validation in `WorkflowsEndpoints` ensures referenced `SubflowKey` exists and (if pinned) that `SubflowVersion` exists.
- Reject self-reference (workflow A having a Subflow node pointing to A) at save time as a quality-of-life check, even though runtime depth cap would catch it.

### Slice 11 — Documentation + samples
- Update [workflows.md](workflows.md) with the new node kind, the `context` vs `workflow` split, and an example shared workflow + composing parent.
- Add a one-page "designing reusable subflows" tip sheet.

### Slice 12 — End-to-end test: parent + subflow with HITL inside child
- Persistence + orchestration integration test that:
  - Defines a parent workflow with one Subflow node.
  - Defines a child workflow with an Agent → HITL → Agent pipeline.
  - Asserts: parent trace is `Running` while child HITL is pending; parent's aggregated `pendingHitl` includes the descendant; answering the descendant HITL drives the parent to `Completed`.
- Second test asserts depth cap: a 4-level chain fails the deepest with `SubflowDepthExceeded` and the failure bubbles to the top-level parent.

## 5. Sequencing notes

- Slices 1–2 can land together (schema + contracts) but no behaviour ships yet.
- Slice 3 depends on 1+2.
- Slices 4 and 5 can be cut as a pair (both touch saga init/completion) or split.
- Slice 6 can land independently of 3–5; it's worth landing early to flush out script-host changes.
- Slices 7–8 need 3–5 to be testable end-to-end, but the API change in 7 can land behind the UI change in 8.
- Slice 9 (editor) gates user-facing usability but doesn't block backend rollout.
- Slice 12 only after 3+4+5+6 are merged.

## 6. Open follow-ups (post-v1)

- Allow subflows to declare custom named output ports (would need an "Exit" node kind and per-port semantics).
- Per-key write allowlists for `setWorkflow` (governance).
- Live shared state (mid-run propagation of `workflow` writes between parent and active children).
- Inline HITL answering directly from the parent trace.
- Visual cycle detection at save time.
- Streaming partial outputs from a long-running subflow.
