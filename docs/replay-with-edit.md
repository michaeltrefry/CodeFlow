# Replay-with-edit on past traces

> Status: Design — 2026-04-27 (T2 from the [Workflow Authoring DX](../docs/authoring-workflows.md) epic).

## Problem

When a workflow run finishes badly — most often a ReviewLoop exhausting on `Rejected` — the
debugging question is invariably "what would have happened if round 3's reviewer had decided
differently?" Today the only way to answer that is to author a fixture by hand, queue mocks one
agent at a time, and dry-run the workflow against guess-mocks; or to re-run the whole workflow
end-to-end and hope the model behaves the same.

Both paths are slow and unreliable. We need to take a *real* past trace, swap one or two recorded
responses, and replay forward — using the original LLM outputs everywhere except the edited
points.

## Insight

We already have everything required:

- Every real saga run persists each agent's exact response in `WorkflowSagaDecisionEntity`
  (`Decision`, `DecisionPayload`, `OutputRef`, ordinal-ordered).
- `DryRunExecutor` already walks a workflow, dequeuing one `DryRunMockResponse` per Agent visit,
  and produces a deterministic `DryRunResult` + event timeline. As of v4 it has full saga parity
  on input/output scripts, decision-output templates, P3 rejection-history, P4 mirror, P5
  port-replacement, HITL form rendering, and retry-context handoff.

Replay-with-edit is the trivial composition of those two: lift recorded responses into the dry-run
mocks dictionary, expose an editor that lets the author swap any cell, and run the dry-run.

## Scope

### MVP (this card)

1. **Backend** — `POST /api/traces/{id}/replay`
   - Read the trace's saga (and any descendant subflow sagas, walked via `parent_trace_id`).
   - For each Agent/HITL `WorkflowSagaDecisionEntity` row, extract `(agentKey, decision, output, payload)`.
     - `output` is the artifact pointed to by `OutputRef`, fetched via `IArtifactStore.ReadAsync`.
     - HITL decisions surface the same shape because the API publishes
       `AgentInvocationCompleted` from `SubmitHitlDecisionAsync` — they're indistinguishable from
       agent responses in the decisions table, which is exactly what we want.
   - Group decisions by `AgentKey` in ordinal order to seed
     `DryRunRequest.MockResponses` (the existing dictionary `agentKey → ordered list`).
   - Apply the request body's `Edits[]` to override specific positions:
     ```json
     { "edits": [{ "agentKey": "reviewer", "ordinal": 3, "decision": "Approved", "output": "looks good", "payload": null }] }
     ```
     The ordinal is the per-agent invocation index (1-based) in the original trace.
   - Optional `WorkflowVersionOverride` lets the author replay against a newer workflow version
     for "what if I edit the script and re-run?". Defaults to the original trace's version.
   - Run `DryRunExecutor.ExecuteAsync` and return:
     ```json
     {
       "originalTraceId": "...",
       "replayState": "Completed",
       "replayTerminalPort": "Approved",
       "replayEvents": [...],
       "drift": { "level": "None|Soft|Hard", "warnings": [...] },
       "diff": { "divergedAt": <ordinal>, "originalEvents": [...], "replayEvents": [...] }
     }
     ```

2. **Drift detection** — before running, compare the original trace's workflow snapshot against
   the version we're replaying against:
   - **Hard drift** — a node referenced in the original decisions has been deleted, or an agent
     no longer exists at the pinned version. Refuse with HTTP 422 and a clear list. Author can
     still force a "best-effort replay" by passing `force=true`.
   - **Soft drift** — workflow version is the same but a script changed, or a port renamed, or
     a new validator catches something. Surface in the response's `drift.warnings[]`; the run
     continues.
   - **None** — workflow version unchanged AND every referenced node still resolves with the
     same shape. The most common case.

3. **UI** — replay panel on the trace detail page (`apps/codeflow-ui/src/app/pages/traces/...`):
   - "Replay with edit" button on terminal traces.
   - Opens a panel showing the agent decision table from the original trace; each row is editable
     (decision dropdown from the agent's declared output ports, output textarea, payload JSON
     editor).
   - Run button calls `POST /api/traces/{id}/replay` with the edits.
   - Result shows the replay's event timeline next to the original's, scrolled so the divergence
     point lines up. The shared trace-inspector component (T1-FOLLOWUP-UI) is the natural target
     here — replay-side rendering should drop into it cleanly.
   - The replayed run is **not persisted as a new saga**. It exists only in the API response; the
     UI keeps it in client state for the session. (Persistence is Phase 2.)

### Out of scope (deliberately)

- **Persisting replays as labeled traces** — the acceptance criterion mentions this, but the MVP
  is more useful per unit-effort if we keep replays ephemeral and let the author re-run on demand.
  Phase 2 of this card if real demand surfaces.
- **Editing scripts inline** — the "replay against newer workflow version" lever already lets an
  author author a new draft, save it, and replay against the draft. Inline script editing is its
  own feature.
- **Replaying mid-trace (start at round N rather than from the beginning)** — the dry-run executor
  walks from the Start node every time, which is the behavior real traces have too. Skipping the
  first N rounds adds correctness risk (workflow-variable seeding, round-id rotation) without
  obvious value. Out unless we hit a concrete debugging case where it matters.

## Implementation slices

| Slice  | Type  | What                                                                                                          |
|--------|-------|---------------------------------------------------------------------------------------------------------------|
| T2-A   | Task  | Design doc + spike (this doc; closed by attaching the link to T2 and unblocking T2-B/C/D).                    |
| T2-B   | Task  | Backend — extract decisions → mocks helper, drift detector, `/api/traces/{id}/replay` endpoint, tests.        |
| T2-C   | Story | UI — replay panel on trace detail page, edit table, side-by-side timeline, drift warning surface.             |
| T2-D   | Task  | Subflow + ReviewLoop coverage — verify decisions across subtree sagas thread into the right mock queues.      |

T2-A unblocks T2-B and T2-D in parallel. T2-C lands after T2-B (needs the endpoint).

## Risks

- **Mock-queue ordering across subflow boundaries.** The dry-run executor recursively re-enters
  subflows; mocks are global per agent key. If the same agent runs in two different subflows the
  responses must be queued in the right order. Mitigation: when extracting decisions, walk all
  descendant sagas in saga-traversal order (the same order DryRunExecutor visits them), and queue
  per-agent in that order. Covered by T2-D.
- **Artifact store read failures on old traces.** Trace deletion is allowed for terminal traces
  and removes artifacts. A trace must not be replayable after deletion. Mitigation: 404 if the
  saga is gone; surface artifact-fetch errors clearly when an artifact has been pruned.
- **Drift detection false positives.** Comparing two workflow definitions byte-for-byte is too
  strict. Use the same "structurally equivalent" check the cascade-bump assistant already uses
  (`WorkflowSagaStateMachine` schema-stable fields, ignoring layout/ordering metadata).
- **Decision ordinal stability.** The `WorkflowSagaDecisionEntity.Ordinal` is per-saga. The
  per-agent ordinal we surface to authors must be derived (count of prior decisions for the same
  agent in saga-traversal order). Easy but worth a test.

## Acceptance against the card

| Card criterion                                                                                  | Plan                                                              |
|-------------------------------------------------------------------------------------------------|-------------------------------------------------------------------|
| Past trace exited on `Exhausted` can be replayed with round 3's reviewer flipped to `Approved`. | T2-B `/replay` endpoint + edit array.                             |
| Original trace is untouched; replayed trace is stored separately and labeled.                   | MVP does not persist (ephemeral). Phase 2 if needed.              |
| Replay engine refuses if the workflow has materially diverged; warns and offers best-effort.    | T2-B drift detector (Hard/Soft/None) + `force=true` opt-in.       |
| Card opens with a design doc link before implementation begins.                                 | This document — attached to the card before T2-B starts.          |

## Open questions

1. Is "replay produces a stored trace" a hard requirement for shipping, or can MVP ship without
   it? The card's acceptance lists it; my recommendation is to defer to Phase 2 unless the author
   explicitly asks otherwise. Captured as a follow-up risk.
2. What should the diff render look like? Side-by-side full timelines are usable but heavy; a
   "first divergence + N events of context" summary may be more readable. Decide during T2-C.
3. Replay against a newer workflow version: should drift detection downgrade to "soft" if the
   only changes are layout-only? Defer to T2-B; the cascade-bump's structural-equivalence check
   already gives us the answer for free.
