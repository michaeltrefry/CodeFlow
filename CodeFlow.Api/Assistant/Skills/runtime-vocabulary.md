---
key: runtime-vocabulary
name: Runtime vocabulary
description: How CodeFlow executes, records, and replays workflow runs.
trigger: concept questions about traces, sagas, replay, drift detection, token tracking, or working-directory layout.
---

# Runtime vocabulary

Load this skill when the user asks how CodeFlow runs, records, or replays
a workflow — anything about traces, sagas, the timeline / canvas view,
replay-with-edit semantics, in-place agent edit + drift, token tracking,
or how code-aware workflows resolve their working directory.

## Traces and sagas

A **trace** is one execution of a workflow, identified by a `traceId`
(GUID). Every trace is driven by a MassTransit **saga** that records each
node entry/exit, every agent decision, every HITL decision, and every
output reference. The trace inspector renders the saga as a timeline view
(per-node entries grouped by invocation) plus a canvas view (the
workflow's node graph with per-node status badges).

When you reference a trace in conversation, prefer its trace id —
`/traces/{traceId}` is the canonical deep link the chat UI auto-renders.

## Token usage tracking

Every LLM round-trip writes a `TokenUsageRecord` with `(traceId, nodeId,
invocationId, scopeChain, provider, model, recordedAt, usage)`. The trace
inspector aggregates these per-call, per-invocation, per-node, per-scope,
and per-trace, with provider + model breakdowns. Cross-trace reporting is
intentionally deferred.

The platform tracks tokens only — **never translate token counts into
currency or quote pricing**. If the user asks for cost, explain the
platform's stance and surface the token totals instead.

## Replay-with-Edit (overview)

From a finished trace, the user can re-run with substitutions: pin
specific node outputs to fixtures or alternate values and replay the saga
substitution-only via the `DryRunExecutor`. The original trace is **not
modified**; the replay lives only in the response. Replay-with-Edit is
substitution-only — it can change a recorded decision / output / payload
but it cannot insert new nodes or rewire the graph; for graph changes the
user authors a new workflow version and runs it.

For procedure (when to call `propose_replay_with_edit`, result branches,
unsupported-kind handling) load the `replay-with-edit` skill.

Swarm nodes are non-replayable — Replay-with-Edit re-executes a Swarm
node fresh on replay rather than substituting cached outputs.

## In-place agent edit and drift detection

Right-clicking an agent node in the workflow editor opens the in-place
agent edit modal scoped to that node. Saving from the modal forks the
agent on the workflow (creates a new agent version pinned to that node
only); publishing back to the canonical agent surfaces a **drift
warning** if the current canonical agent diverges from the version
captured in the open trace.

Drift detection is one-way: the editor compares "the version pinned by
this trace" against "the latest version of this agent's key in the
library" and warns when they diverge. The workflow itself is unaffected
— the trace's pinned versions don't change.

## Code-aware workflows and the working directory

Workflows that operate on source code use a per-trace working directory
derived from `Workspace:WorkingDirectoryRoot` (default
`/app/codeflow/workdir`). Workflows take a `repos[]` input convention;
each repo is checked out into the per-trace workdir before agents run.

Operator overrides:
- `Workspace__WorkingDirectoryRoot` — environment-variable override of
  the default root path. Locked at deploy; not editable from the admin
  UI (sc-... 2026-04-27 hardening).

When agents in a code-aware workflow invoke host tools (`read_file`,
`apply_patch`, `run_command`), those tools resolve paths relative to the
trace's workdir. The same `repos[]` input lets the runtime materialize
the right checkout state per trace.

## Trace deep links the assistant cites

- `/traces/{traceId}` — trace inspector (timeline + canvas).
- `/traces/{traceId}/nodes/{nodeId}` — focused inspector for one node's
  invocations.
- `/agents/{agentKey}` — agent editor (latest version) — link this when
  pointing the user at a misbehaving agent's prompt.

These render automatically in chat as clickable chips.
