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

## Code-aware workflows: workspace and repositories

Code-aware workflows expose two framework-managed pieces of per-trace
state on the `workflow` bag, both backed by typed saga fields so they
survive subflow boundaries:

### `workflow.traceWorkDir` — the per-trace workspace path

Computed at trace launch as `<Workspace:WorkingDirectoryRoot>/<traceId.N>`
(default root is `/workspace`; override via the
`Workspace__WorkingDirectoryRoot` environment variable, locked at deploy).
The directory is created on disk by `TracesEndpoints.CreateTraceAsync`
before publish. Read in templates as `{{ workflow.traceWorkDir }}` and
in scripts as `workflow.traceWorkDir`.

`traceWorkDir` is in `ProtectedVariables.ReservedKeys` — `setWorkflow`
from a script or agent rejects writes. Subflow children inherit the
parent's path verbatim (children share the parent's workspace; the path
is NOT recomputed from the child trace id).

When agents in a code-aware workflow invoke path-jailed host tools
(`read_file`, `apply_patch`, `run_command`), those tools are scoped to
this directory.

### `workflow.repositories` — the per-trace VCS allowlist

A JSON array of `{ "url", "branch"? }` objects declaring the repos this
trace operates on. The `vcs_*` host tools (`vcs.clone`, `vcs.get_repo`,
`vcs.open_pr`) enforce the allowlist — calls against an undeclared
`(owner, name)` return `repo_not_allowed`.

Three ways `workflow.repositories` gets populated:

1. **Workflow input convention.** Declare an input with key
   `repositories` and `kind: Json`. The save-time validator enforces the
   shape `[{url, branch?}]`. At launch, `TracesEndpoints` resolves the
   input and routes the value into the `workflow` bag (NOT `context`).
2. **Trace-launch override.** The launcher's `inputs.repositories`
   payload supersedes the input default — same routing.
3. **Mid-flight via `setWorkflow`.** An agent that discovers a new repo
   (or that wants to widen the allowlist) calls
   `setWorkflow('repositories', [...])`. **Note:** the change takes
   effect on the *next* dispatch, not the current turn — the
   `BuildToolExecutionContext` snapshot for the in-flight invocation
   was already taken from the saga's prior state.

`setContext('repositories', ...)` does NOT widen the allowlist. The
saga's typed `RepositoriesJson` field is fed only from the workflow.*
bag.

Subflow children inherit the parent's allowlist verbatim through the
saga field — child workflows do not need to redeclare `repositories`.

### Mutation patterns for code-aware agents

A `code-setup` agent typically mutates `workflow.repositories` mid-turn
to attach `localPath` and `featureBranch` after cloning:

```
setWorkflow('repositories', [
  { "url": "...", "branch": "main",
    "localPath": "/workspace/<traceId-N>/<repo>",
    "featureBranch": "<branch_name-output>" }
])
```

A `publish` agent appends `prUrl` after `vcs.open_pr` succeeds. The
saga's happy-path workdir cleanup hook deletes the workdir on
`Completed` only when every entry in `workflow.repositories` has a
non-empty `prUrl`; failed runs leave the workdir for forensics.

## Trace deep links the assistant cites

- `/traces/{traceId}` — trace inspector (timeline + canvas).
- `/traces/{traceId}/nodes/{nodeId}` — focused inspector for one node's
  invocations.
- `/agents/{agentKey}` — agent editor (latest version) — link this when
  pointing the user at a misbehaving agent's prompt.

These render automatically in chat as clickable chips.
