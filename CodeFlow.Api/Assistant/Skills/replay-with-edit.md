---
key: replay-with-edit
name: Replay with Edit
description: Procedure for proposing a Replay-with-Edit on a past trace.
trigger: user wants to replay a past trace with a substitution edit ("what if X had been Y", "rerun with the reviewer approving instead").
---

# Replay with Edit

Load this skill when the user (or your own diagnosis) surfaces a
candidate substitution on a past trace — "if the reviewer had approved
instead of rejected, the loop would terminate", "if the writer had
emitted JSON instead of prose, the parser wouldn't have failed". The
proposal goes through one tool: `propose_replay_with_edit`.

## When to invoke

When `diagnose_trace` (or your own analysis) surfaces a candidate
substitution, invoke `propose_replay_with_edit` with:
- `traceId` — the trace to replay.
- `edits` — a small array; each entry names an `agentKey` and `ordinal`
  (1-based per-agent invocation in the recorded trace) and supplies at
  least one of `decision`, `output`, or `payload`.

The tool runs admission-only validation (does the agentKey exist in the
trace? does the ordinal map to a real recorded invocation? is the
substitution kind supported?). It does NOT run the replay — when
`status: "preview_ok"` comes back the chat UI surfaces a chip the user
clicks to apply, and the apply endpoint runs the replay through the
`DryRunExecutor`.

## Result branches

- **`status: "preview_ok"`** → STOP. The chip is in front of the user.
  Do not call the tool again or take further action until the user
  responds. If the user says they don't see a chip, that is a UI render
  concern, not a signal to re-invoke.
- **`status: "invalid"`** → fix the `(agentKey, ordinal)` pairs against
  the surfaced `recordedDecisions` list and re-invoke. Don't retry
  blindly — match each edit to a real recorded invocation.
- **`status: "unsupported"`** → tell the user which substitution kind
  isn't supported and offer the closest workable alternative. Common
  unsupported case: substituting on a synthetic Subflow boundary marker
  — the workable alternative is editing inside the child trace. Swarm
  nodes are non-replayable; a Swarm node always re-executes fresh on
  replay.
- **`status: "trace_not_found"`** → confirm the trace id with the user.
  Don't guess a different one.

## Constraint: substitution-only

Replay-with-Edit is **substitution-only**. You can change a recorded
decision / output / payload but you cannot:
- Insert new nodes.
- Rewire edges.
- Change a workflow's port wiring.
- Force a different code path that the original trace didn't take when
  every recorded decision is held constant.

For graph changes, the user authors a new workflow version and runs it
fresh — Replay-with-Edit is not the right tool.

## Keep edits minimal

Propose the smallest edit set that demonstrates the hypothesis. A
single edit ("if reviewer ordinal 2 had decided Approved instead of
Rejected") is more informative than a sweeping rewrite. The user can
chain follow-up replays if they want to test combinations.
