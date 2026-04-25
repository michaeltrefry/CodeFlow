# Port model — user-defined ports

This is the canonical reference for how port routing works in CodeFlow after the port-model
redesign (2026-04). It supersedes the older fixed-enum model that constrained ports to
`Completed` / `Approved` / `Rejected` / `Failed` / `Escalated`.

## The core rule

Decisions are user-defined; ports are decisions; everything else is inherited or implicit.

A port is just a string the workflow author chose. The runtime, validator, and editor never
check port names against a global allowlist — they check edges against the source node's
*declared* ports. The shape of a node's port set depends on what kind of node it is.

## Port sets by node kind

| Kind | Declared ports come from… | Implicit | Synthesized |
|------|---------------------------|----------|-------------|
| `Start`, `Agent`, `Hitl` | The pinned agent's `outputs[].kind` (cross-validated at save). | `Failed` | — |
| `Logic` | The author's `outputPorts` array (free text). | `Failed` | — |
| `Subflow` | The pinned child workflow's terminal-port set (computed). | `Failed` | — |
| `ReviewLoop` | The pinned child workflow's terminal-port set, **minus** the configured `loopDecision`. | `Failed` | `Exhausted` |

A **terminal port** is any `(node, portName)` pair on a workflow node whose port has no
outgoing edge — it's a designed exit. The implicit `Failed` port is *not* a terminal port; it's
an error sink, not a designed exit. ReviewLoop's synthesized `Exhausted` port is a terminal
port when not wired.

## The implicit `Failed` port

Every node implicitly has a `Failed` port. The editor renders it as a wirable handle (visually
distinct from declared ports — italicized, danger-color); the validator never requires
declaring it; the runtime routes to it on agent invocation failures and uncaught exceptions.

If wired, `Failed` routes runtime errors to a custom recovery path. If unwired, the workflow
terminates with `FailureReason` set.

**Authors must not declare `"Failed"` in `outputPorts`.** The save-time validator rejects it:
declaring an implicit port is an authoring error.

## ReviewLoop port semantics

ReviewLoop nodes synthesize two ports the inner workflow does not own:

- **`Exhausted`** fires when `reviewMaxRounds` is reached without a clean exit. Reserved on
  ReviewLoop's port set; never declared explicitly.
- **The configured `loopDecision` port** — when the inner workflow's terminal port name
  matches `loopDecision`, the ReviewLoop iterates instead of exiting. Defaults to `"Rejected"`
  for backwards readability but accepts any string. `"Failed"` is reserved (error propagation)
  and not allowed.

The ReviewLoop's exit ports are: child terminal ports ∪ `{Exhausted}` minus `{loopDecision}`,
plus the implicit `Failed`.

## Cross-validation

For `Start` / `Agent` / `Hitl` nodes, the validator requires `outputPorts` ⊆ the pinned
agent's declared output kinds. Drift (an agent had an output removed but the workflow node
still references it) surfaces as a save-time error naming both sets and as an inline "stale"
chip in the editor.

For `Subflow` / `ReviewLoop` nodes, the validator computes the child workflow's terminal-port
set and requires every edge `fromPort` to be in that set (or, for ReviewLoop, in the
synthesized union with `{Exhausted, loopDecision}`).

## Editor affordances

- **Agent/Hitl/Start port pickers** are read-only lists derived from the pinned agent's
  declared outputs. Authors can't type port names; they pick the agent and the ports follow.
- **Subflow / ReviewLoop port pickers** are read-only and derived from the pinned child
  workflow's terminal-port set (via `GET /api/workflows/{key}/{version}/terminal-ports`).
- **"Update to latest version…"** is a per-node right-click action that loads the latest
  version of the pinned agent or child workflow, shows a port-set diff (added / removed / what
  edges would break), and only applies on confirm.
- **Broken-edge inline warnings** flag any edge whose `fromPort` is no longer in the source
  node's declared port set, so authors see drift before saving.

## What was removed

- The `AgentDecisionKind` enum (`Completed` / `Approved` / `Rejected` / `Failed`) is gone.
  `AgentDecision` is now `(string PortName, JsonNode? Payload)`. Failure carries
  `payload.reason`.
- The `Escalation` node kind is gone. If you want escalation semantics, declare an output
  named `Escalated` on the relevant agent and wire it.
- The fixed `SubflowAllowedPorts = [Completed, Failed, Escalated]` and
  `ReviewLoopAllowedPorts = [Approved, Exhausted, Failed]` constants are gone.

## Migration from the old model

This was a breaking cutover; no production data existed. The dev DB is wiped and starter
workflow JSONs were rewritten to drop `Failed` from declared `outputPorts` and from agent
declared `outputs`. The transformation that produced the new starters lives in
`starter_workflows/_rewrite_for_port_model.py` for reference.

## Quick reference: validator rules

1. `Failed` may not appear in any node's `outputPorts` (it's implicit).
2. `Exhausted` may not appear in any node's `outputPorts` (reserved for ReviewLoop synthesis).
3. For agent-bearing nodes (`Start` / `Agent` / `Hitl`), `outputPorts` must be a subset of the
   pinned agent's declared output kinds.
4. Edges may always use `fromPort = "Failed"` regardless of declarations (the implicit handle).
5. Edges from a `Subflow` node must use a port in the child's terminal-port set (or `Failed`).
6. Edges from a `ReviewLoop` node must use a port in the child's terminal-port set ∪
   `{Exhausted, loopDecision}` (or `Failed`).
7. Two edges may not leave the same `(node, port)` pair.
