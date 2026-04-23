# Workflows

Workflows describe how a trace moves between agents and logic checks to get a job done. The UI is a ComfyUI-style drag-and-drop canvas at `/workflows/:key/edit`; the underlying data model is a directed graph of **nodes** connected by **edges** that reference named **output ports**.

## Node kinds

Every workflow is composed of nodes. The canvas palette exposes seven kinds:

| Kind | Purpose | Has agent | Has script |
|---|---|---|---|
| `Start` | Single entry point for any trace. Exactly one per workflow. | Yes | No |
| `Agent` | Dispatches an agent via `AgentInvokeRequested`. | Yes | No |
| `Hitl` | Dispatches a human-in-the-loop agent that pauses the saga until a human submits a decision via `/api/traces/:id/hitl-decision`. | Yes | No |
| `Logic` | In-process JavaScript router. Does not dispatch an agent; evaluates a script that picks an output port. | No | Yes |
| `Escalation` | Fallback when a round count overflows. At most one per workflow. | Yes | No |
| `Subflow` | Invokes another workflow as a reusable building block and waits for its terminal state. | No | No |
| `ReviewLoop` | Invokes another workflow up to `MaxRounds` times; each round's output becomes the next round's input until the child returns `Approved` (or rounds run out). | No | No |

Every node has a stable `Id` (GUID) that persists across saves, a set of named **output ports**, and layout coordinates `(LayoutX, LayoutY)`.

## Edges and ports

An edge is `(FromNodeId, FromPort) → (ToNodeId, ToPort)` plus a `RotatesRound` flag and a `SortOrder`. Ports are strings — for agent nodes the port name equals the returned `AgentDecisionKind` (`Completed`, `Approved`, `Rejected`, `Failed`). Logic nodes declare their ports explicitly.

**Connection rules enforced by the editor:**

- At most one outgoing edge per output port.
- Fan-in is allowed on input ports.
- Escalation nodes have no outgoing edges.
- The `ToPort` defaults to `"in"` — every non-Start node implicitly has one input port.

If the runtime follows an edge that would push the round count past `MaxRoundsPerRound` (and the edge does not rotate), it routes to the Escalation node instead of the intended target. If no Escalation node is configured, the trace terminates as `Failed`.

## Context: local (`context`) vs. shared (`global`)

Every saga carries **two** context bags that are exposed to Logic-node scripts and to agent prompt templates:

| Name | Scope | Lifetime | Writable from inside? |
|---|---|---|---|
| `context` | Current workflow's **local** state (the saga's `inputs_json`). | Per saga (top-level or a single subflow invocation). | Yes — `setContext('foo', x)` |
| `global` | **Shared** bag inherited between parent and descendant subflows. | Snapshot taken when a Subflow node fires; the child's final `global` is shallow-merged back into the parent's `global` on completion. | Yes — `setGlobal('foo', x)`; writes bubble up on subflow completion only. |

For a **top-level** workflow:

- `context` is the workflow's input bag (see below) — this is the current behaviour used by every existing workflow.
- `global` starts empty (or is seeded by whatever initial values the API caller supplies when launching the trace).

For a **subflow** invocation:

- `context` is local to the child saga — it starts empty and is discarded when the child terminates.
- `global` is a **snapshot** of the parent's `global` taken at the moment the Subflow node fires; `setGlobal` writes land on that snapshot and are shallow-merged (last-write-wins per top-level key) back into the parent's `global` when the child completes. Effects only become visible to the parent on completion — there is no live shared store.

Agent prompt templates can reference both namespaces: `{{context.repoType}}` reads the current saga's local context, `{{global.resolvedSpec.engine}}` reads the shared bag.

```js
// Logic node example using both scopes
log('Attempt ' + ((context.attempt || 0) + 1));
setContext('attempt', (context.attempt || 0) + 1);   // local to this saga
setGlobal('lastResult', input.summary);              // visible to ancestors on completion
setNodePath('Continue');
```

## Workflow inputs (local context)

A workflow can declare an ordered list of typed **inputs**:

```json
{
  "key": "gitRepo",
  "displayName": "Git repository path",
  "kind": "Text",
  "required": true,
  "defaultValueJson": null,
  "description": "Absolute path on the host machine.",
  "ordinal": 0
}
```

`Kind` is `Text` or `Json`. At trace launch, the UI renders a form from the schema and posts concrete values on `CreateTraceRequest.inputs`. The resolved map is frozen onto the saga state for the duration of the run; every `AgentInvokeRequested` published during the trace includes it as `ContextInputs`. Logic-node scripts read it through a frozen `context` object, and agent prompt templates can reference it through a reserved flattened variable namespace such as `{{context.gitRepo}}` or `{{context.target.path}}`.

Initial inputs are seeded once at trace launch. Agent and HITL nodes cannot mutate context. Logic nodes can write top-level keys via `setContext` (local) or `setGlobal` (shared) — see above.

## Logic nodes

Logic nodes execute JavaScript inside a sandboxed Jint engine. They route traffic by choosing an outgoing port and may write back into workflow context. The script receives:

- `input` — parsed JSON payload from the upstream node's output. For agent nodes, this is usually the decision payload; for plain-text agent output, the runtime wraps it as `{ "text": "…" }`.
- `context` — frozen object containing the workflow's current context. Direct mutation throws in strict mode — use `setContext` to write.
- `setNodePath(portName)` — picks the outgoing edge. Must be called at least once. Must reference a port declared on the node.
- `setContext(key, value)` — writes a top-level key into the workflow context. `value` must be JSON-serializable. The write is applied to the saga's context after a successful evaluation; all downstream nodes (and any re-entry of upstream nodes via a back-edge) see the new value as `context.key`. Writes are dropped if the script fails. Combined writes across a Logic chain are capped at 256 KB of serialized JSON.
- `log(message)` — appends to the trace log for the current evaluation.

### Loop accumulator pattern

To keep state across a loop — for example, an interviewer agent looping through an HITL with a running transcript — place a Logic node between the HITL and the back-edge:

```js
var transcript = (context.transcript || []).slice();
transcript.push({ q: input.question, a: input.answer });
setContext('transcript', transcript);
setContext('turn', (context.turn || 0) + 1);
setNodePath('NextTurn');
```

The interviewer's prompt template can then reference `{{context.transcript}}` and `{{context.turn}}` on each loop iteration.

### Example

A Start agent that produces `{"kind":"NewProject"|"feature"|"bugfix", ...}` can fan out to three downstream flows through a Logic node:

```js
if (input.kind === 'NewProject') {
  setNodePath('NewProjectFlow');
} else if (input.kind === 'feature') {
  setNodePath('FeatureFlow');
} else if (input.kind === 'bugfix') {
  setNodePath('BugFixFlow');
} else {
  log('unknown kind: ' + input.kind);
  setNodePath('Failed');
}
```

The Logic node's inspector declares four ports — `NewProjectFlow`, `FeatureFlow`, `BugFixFlow`, `Failed` — wired to the appropriate downstream agents.

### Sandbox limits

The script host is hardened:

- **Recursion depth:** 64
- **Statement count:** 10,000
- **Memory:** 4 MB
- **Wall-clock timeout:** 250 ms
- **String compilation:** disabled — `eval` and `new Function(...)` throw.
- **CLR interop:** disabled — no access to .NET types.
- **No network, filesystem, database, or MCP access.** Those are the job of agents.
- **Strict mode** is always on.

### Failure handling

If the script throws, times out, exceeds limits, omits `setNodePath`, or chooses an undeclared port, the Logic node's effective output port is `Failed`. The runtime looks for an edge from the node on port `Failed`; if present, routing continues along it. If absent, the trace terminates as `Failed`.

Every Logic evaluation produces a `LogicEvaluationRecord` on saga state containing the node id, chosen port, duration, captured log messages, and any failure reason — retrievable via the trace detail API for debugging.

### Validating scripts

`POST /api/workflows/validate-script` with `{ "script": "…", "declaredPorts": ["A", "B"] }` returns `{ "ok": true }` or `{ "ok": false, "errors": [...] }`. The editor's "Validate" button calls this on demand; saving the workflow also runs the parse check as part of workflow validation.

## Agent-attached routing scripts

Agent, HITL, Escalation, and Start nodes may carry an **optional** `Script` that picks the outgoing port after the agent completes. The sandbox is **identical** to the Logic node sandbox — same globals (`input`, `context`, `setNodePath`, `setContext`, `log`), same limits, same security posture.

### When the script runs

After the agent emits `AgentInvocationCompleted`, the saga:

1. Finds the source node.
2. If the node has a non-empty `Script`, reads the agent's output artifact as JSON, attaches `input.decision` (the AgentDecisionKind name: `Completed`, `Approved`, `Rejected`, `Failed`) and `input.decisionPayload` (the raw decision payload or `null`), and evaluates the script with the workflow's current `context`.
3. Uses the port chosen by `setNodePath` to find the outgoing edge.
4. If the script fails (throws, times out, omits `setNodePath`, or picks an undeclared port), routing **falls back** to the `AgentDecisionKind`-named port carried on the completion message (i.e., the pre-script behavior).

`setContext` writes are merged into the saga's context and flow into every downstream dispatch — same semantics as Logic node writes.

### Example: Answer-vs-Exit HITL routing

A HITL node can fan out based on the human's decision without a separate Logic node:

```js
if (input.decision === 'Approved') {
  setNodePath('Answer');
} else {
  setNodePath('Exit');
}
```

Declare the node's `OutputPorts` as `["Answer", "Exit"]` and wire each port to the appropriate downstream.

### Example: interviewer loop with accumulated transcript

A Start/Agent node that should loop back through a HITL while accumulating the Q&A:

```js
var transcript = (context.transcript || []).slice();
transcript.push({ q: input.question, a: input.answer });
setContext('transcript', transcript);
setNodePath('NextTurn');
```

The interviewer's prompt template can reference `{{context.transcript}}` on every iteration.

### When to use a Logic node vs an agent-attached script

- **Agent-attached script:** the routing decision depends on the agent's own output or decision kind, and you want the script co-located with the agent that produced it.
- **Logic node:** the routing decision depends purely on `context` (no upstream agent output to inspect), or you want a reusable branching node independent of any specific agent.

Evaluations of agent-attached scripts record a `LogicEvaluationRecord` against the source node id, so they appear in the trace detail alongside Logic node evaluations.

## Subflows (composing workflows)

A **Subflow** node invokes another workflow as a reusable building block. The composing parent treats it like an agent node: one input artifact in, one output artifact + decision out. See [subflows.md](subflows.md) for the full design.

**Defining a Subflow node.** In the canvas, drag the Subflow node onto your workflow and fill in the inspector:
- **Workflow** — the child workflow's key (must already exist; save-time validation rejects unknowns).
- **Version** — either a specific pinned version or "Latest at save" (null), which is resolved to the current latest at parent-workflow save time, just like agent node versions.
- **Output ports** — always `Completed`, `Failed`, `Escalated`. Route each to its downstream handler as you would an agent decision.

**What happens at runtime:**
1. The parent saga reaches the Subflow node and publishes `SubflowInvokeRequested` carrying the parent's current `global` snapshot, the input artifact, and a fresh `ChildTraceId`.
2. A **child saga** is created with `TraceId = ChildTraceId`. The child has its own local `context` (starts empty) and its own copy of `global` (the snapshot from the parent). It runs its workflow to completion as a normal trace.
3. Inside the child, any `setGlobal('key', value)` writes land on the child's working `global`.
4. When the child reaches a terminal state (`Completed`, `Failed`, or `Escalated`) it emits `SubflowCompleted` carrying its final `global` and its last output artifact.
5. The parent saga **shallow-merges** (last-write-wins per top-level key) the child's `global` into its own, appends a synthetic decision to its history, and routes from the Subflow node's matching output port.

**Entry: the child's Start node.** A subflow workflow must declare exactly one `Start` node (same rule as any other workflow — enforced by the save-time validator and by `Workflow.StartNode`). The child Start receives:

| Surface | Value at child Start |
|---|---|
| Input artifact (`input` in scripts, `{{input}}` / `{{input.*}}` in agent prompts) | The artifact the parent's Subflow node received as *its* input — i.e. the upstream parent node's output. |
| `context` / `{{context.*}}` | **Empty.** The child's local context starts at `{}`. |
| `global` / `{{global.*}}` | The parent's `global` snapshot taken at the moment the Subflow node fired. |

**The child workflow's declared `inputs` schema is ignored in subflow mode.** The launch form rendered from `WorkflowInput[]` only applies to top-level trace kickoffs via `POST /api/traces`. A subflow invocation bypasses it entirely — no form, no defaults, no required-input checks. If you want structured, named values visible inside the child, either:

- Populate `global.*` before the Subflow node fires (via `setGlobal` on an upstream Logic or agent-attached script), or
- Structure them into the input artifact's JSON so the child can read them as `input.foo`, `input.foo.bar`, etc.

A consequence: **a workflow designed to be callable both top-level and as a subflow shouldn't rely on its declared `inputs`** — or it needs a Start-adjacent Logic node that defensively seeds `context.*` from either `input` or `global.*` depending on which call path populated them.

**Recursion cap: depth 3.** Top-level workflows run at depth 0; each nested Subflow increments by 1. A chain is legal up to root → A → B → C (depth 3). If a Subflow spawn would push depth to 4, the parent saga fails immediately with `FailureReason` starting with `SubflowDepthExceeded:` and no child is spawned.

**HITL inside a subflow.** Pending HITL tasks anywhere in a trace's subtree are surfaced on every ancestor trace's `pendingHitl` list. Each entry includes an `originTraceId` and a `subflowPath` (ordered list of workflow keys from root → owning saga) so the UI can group and label them. Answering a HITL uses the existing global-by-task-id endpoint — no changes needed.

### Exiting a subflow

There is no explicit "exit node" marker — a child saga terminates as soon as it walks off the graph, i.e. reaches a node whose chosen output port has no outgoing edge. Which port gets chosen determines the child's terminal state and the decision kind propagated back to the parent:

| Child's last agent emits | Unwired port behaviour | Child terminal state | `SubflowCompleted.Decision` |
|---|---|---|---|
| `Completed` | Legal clean exit | `Completed` | `Completed` |
| `Approved` | Legal clean exit (signals approval to the parent) | `Completed` | `Approved` |
| `Rejected` | Legal clean exit (signals "revise" to a ReviewLoop parent, or a terminal rejection otherwise) | `Completed` | `Rejected` |
| `Failed` | Terminates the child as `Failed` with a "no outgoing edge" reason | `Failed` | `Failed` |
| Escalation node resolves with `Rejected` | Escalation-recovery flow | `Escalated` | (unchanged; escalation path) |

A plain `Subflow` parent maps the child's terminal state to its three output ports (`Completed` / `Failed` / `Escalated`), so `Approved` / `Rejected` unwired exits both surface as `Subflow.Completed` — the `Decision` field is metadata the parent can inspect but not a distinct port. A `ReviewLoop` parent uses `Decision` directly: `Approved` or `Completed` → Approved port; `Rejected` with rounds remaining → next round; `Rejected` on the last round → Exhausted port; `Failed` / missing decision → Failed port.

The child's **last output artifact** — the artifact produced by the final agent before termination — becomes the input to whatever the parent routes to next. The child's final `global` is shallow-merged back into the parent's `global` regardless of which port fired.

**Common patterns:**

- **Single happy exit.** Route every success path to one final agent, and leave its `Completed` port unwired. That node becomes the de-facto exit — when it completes, the child terminates `Completed` and the parent continues from `Subflow.Completed`.
- **Multi-exit.** Any node with an unwired `Completed`, `Approved`, or `Rejected` port is a legal exit. The first one the saga reaches terminates the subflow; there is no priority mechanism beyond execution order.
- **ReviewLoop child pattern.** A reviewer agent inside a ReviewLoop child typically leaves its `Approved` and `Rejected` ports unwired — the parent ReviewLoop routes on the emitted decision kind to either exit Approved or spawn the next round. No wiring needed inside the child for the loop to work.
- **Explicit failure exit.** Wire the error-handling branch to an agent whose `Failed` port is left open. That terminates the child `Failed`, routing the parent from `Subflow.Failed`.
- **Use Escalation for recoverable failures only.** A child `Escalated` terminal is specifically the Escalation node resolving with `Rejected`. Reserve it for cases where the child truly couldn't recover despite escalation — for ordinary failure, use `Failed`.

**Gotchas:**

- Only `Completed` / `Approved` / `Rejected` have the "unwired = clean exit" semantics. Unwired `Failed` still fails the child (that's the design — `Failed` means something actually went wrong). Any other custom port name that's unwired also fails the child with a "no outgoing edge" reason.
- The save-time validator rejects unknown Subflow output ports on the parent (only `Completed`, `Failed`, `Escalated` are allowed on the Subflow node itself), but doesn't verify the child workflow's exit shape — a child with only unwired `Failed` ports will always return `Failed` to the parent.

### Example: shared review subflow

Define a small reusable review workflow `quick-review-v1`:

```
Start(reviewer-agent)  → Completed → [terminal]
```

Now compose a larger `publish-flow`:

```
Start(writer-agent)  → Completed → Subflow(key="quick-review-v1", version=null)
                                    Completed → Publish(publisher-agent) → [terminal]
                                    Failed    → HandleFailure(fallback-agent) → [terminal]
                                    Escalated → Escalation(editor-in-chief) → [terminal]
```

At save time, `publish-flow`'s Subflow node has its `SubflowVersion` rewritten from `null` to the latest `quick-review-v1` version (e.g. `3`), so re-running a saved parent version is reproducible even if the child workflow gains new versions later.

Re-save the parent workflow (creating `publish-flow` v2) and the null-version slot re-resolves to whatever is then-latest — same behaviour as agent versions.

### `setGlobal` propagation example

```
root-flow (depth 0)
  └─ Subflow  → child-flow (depth 1)
                  ├─ Logic[setGlobal('sharedFact', 'learned')]
                  └─ terminal(Completed)
```

- While `child-flow` runs, its working `global` gets `{ sharedFact: 'learned' }`.
- On terminal, `SubflowCompleted.SharedContext = { sharedFact: 'learned' }`.
- The parent saga in `root-flow` shallow-merges that into its own `global` before routing onward. Downstream nodes in `root-flow` see `global.sharedFact === 'learned'`.

### Tips for designing reusable subflows

- **Keep the shared surface small.** Prefer reading the input artifact and returning an output artifact over reaching into `global`. A narrow interface makes the subflow easier to reuse.
- **Don't rely on parent `context`** — a subflow starts with an empty `context`. If you need something from the parent, it must flow in as the input artifact or live on `global`.
- **Treat `setGlobal` as an output**, not an event. Writes only become visible on completion, and only once. Parallel subflows would race; don't design around mid-run shared state.
- **Stay under depth 3.** If you find yourself wanting a fourth level, flatten the deepest composition into the caller instead.
- **Version pins should be explicit for anything production-critical.** Leave `null` ("latest at save") for subflows you iterate on often; pin an integer when you need reproducibility across re-saves.
- **Self-references are rejected at save time.** A workflow can't have a Subflow node that points at itself; use an Escalation node for guarded self-invocation patterns if you need them.

## Review Loops (bounded iterate-until-approved subflows)

A **ReviewLoop** node is a specialized subflow that re-invokes a child workflow up to `MaxRounds` times. Each round runs the child end-to-end; the child's terminal effective port name drives the loop. The comparison is against a configurable **`LoopDecision`** (default `"Rejected"`):

| Child terminal signal | Rounds remaining | Outcome |
|---|---|---|
| Effective port equals `LoopDecision` | > 0 | **Advance to the next round.** Round N's output becomes Round N+1's input artifact |
| Effective port equals `LoopDecision` | 0 (last round) | Exit the `Exhausted` port |
| `Decision` is `Approved` / `Completed` (and port didn't match `LoopDecision`) | any | Exit the `Approved` port |
| `Decision` is `Failed`, or port is `Failed` / `Escalated` | any | Exit the `Failed` port |
| `Decision` is `Rejected` and `LoopDecision` ≠ `"Rejected"` | any | Exit the `Failed` port (bare Rejected with a custom LoopDecision is a terminal verdict, not an iterate) |

A ReviewLoop node's **output ports are fixed**: `Approved`, `Exhausted`, `Failed`. The canvas editor enforces that.

**Effective port** means: the port the child saga actually picked when terminating. If the terminal node had a routing script, that's the script's `setNodePath(...)` choice; otherwise it's the port name derived from the agent's `AgentDecisionKind`. This lets routing-script patterns like a socratic-interview loop drive iteration off any port name without the underlying agent decision kind needing to match.

### Round variables

Every agent prompt and logic script in the child workflow can reference three additional template variables / Jint bindings:

| Name | Scope | Meaning |
|---|---|---|
| `{{round}}` / `round` | Prompt template + JS | 1-indexed round number (1 on the first pass). |
| `{{maxRounds}}` / `maxRounds` | Prompt template + JS | The configured `MaxRounds` on the parent ReviewLoop node. |
| `{{isLastRound}}` / `isLastRound` | Prompt template + JS | Boolean; true when `round === maxRounds`. |

Outside a ReviewLoop, JS bindings default to `round = 0`, `maxRounds = 0`, `isLastRound = false` (so scripts shared between ReviewLoop and non-ReviewLoop children don't hit `ReferenceError`). The prompt-template variables are simply unresolved when not in a ReviewLoop.

### Global context across rounds

Each round's child saga starts with a **fresh local `context`** but inherits the carried `global` bag from the prior round (plus the parent's snapshot at node entry). `setGlobal` writes from round N are visible to round N+1's prompts and scripts via `global.*`. On loop exit, the accumulated global is shallow-merged back into the parent saga.

### Configuration

A ReviewLoop node has four settings:

- `SubflowKey` — child workflow key (required).
- `SubflowVersion` — pinned child version, or `null` for "latest at save" (resolved identically to plain Subflow nodes at save time).
- `MaxRounds` — integer in `[1, 10]`. Required.
- `LoopDecision` — port name that triggers another iteration when the child's effective terminal port matches. Defaults to `"Rejected"`. Case-sensitive, 1–64 chars, cannot be `"Failed"` or `"Escalated"` (those are reserved for error propagation). Use a custom value like `"Answered"` for socratic-style loops where the routing script picks a non-standard port name.

Self-references are rejected at save time, same rule as Subflow.

**When to override `LoopDecision`:** keep the default when the child workflow's last agent emits `Rejected` directly via the submit tool (the common case for structured reviewer agents). Override it when the child uses a routing script to select a non-standard port name as the loop signal — e.g. a HITL interviewee that picks `setNodePath('Answered')` for "I answered, ask me another question."

### Depth

A ReviewLoop node counts toward `MaxSubflowDepth = 3` (the same limit as Subflow). All rounds of a single ReviewLoop run at the same nesting depth — iterating doesn't accumulate depth. A ReviewLoop inside a Subflow inside a ReviewLoop is legal as long as nesting ≤ 3.

### Example: draft → critique → revise with `{{isLastRound}}`

Child workflow `critique-revise` (pseudocode for clarity):

```
Start ─▶ Writer (agent) ─▶ Reviewer (agent)
                           │
                           │  Reviewer prompt:
                           │    You are reviewing a draft.
                           │    {{#if isLastRound}}
                           │    This is round {{round}} of {{maxRounds}} — the last one.
                           │    You must Approve or Reject; do not ask for more revisions.
                           │    {{/if}}
                           │    Draft: {{input}}
                           ▼
                  emits Approved / Rejected / Failed
```

Parent workflow that uses it:

```
Start ─▶ ReviewLoop(critique-revise, MaxRounds=3)
            ├─ Approved  ─▶ Publish
            ├─ Exhausted ─▶ Human Editor (Hitl)
            └─ Failed    ─▶ Escalation
```

With `MaxRounds = 3`, the reviewer agent sees `isLastRound === true` only on round 3. Authors can use that to change tone, skip low-priority nits, or force a terminal decision.

### Trace UI

Each round is a separate child saga with its own `TraceId`, so every round's agent invocations, logic evaluations, and HITL tasks show up in its own trace detail. The parent trace's **Review loops** section lists each round with a deep link and a `Rejected` badge on the rounds that looped. The final round's outcome is visible on the parent's decision history (synthetic record on the ReviewLoop node showing the mapped port).

### When to use ReviewLoop vs. a cyclic Subflow

- Prefer **ReviewLoop** whenever the loop's termination condition is "reviewer approved or gave up." The node handles the round cap and feeds round N's output as round N+1's input for free, and prompts get `{{isLastRound}}` without any wiring.
- Prefer a plain **Subflow** inside a cyclic edge when you need custom per-round logic that isn't just "retry with feedback" — e.g. N-queens backtracking, or work that fans out in parallel.

## Workflow versions

Workflows are immutable by version. Every Save creates a new `(key, version)` row; in-flight traces continue running against the version they launched with. The editor opens the latest version by default; older versions remain available via `/api/workflows/{key}/versions` and the detail page's version picker.

## Runtime shape

At a glance:

- `Workflow` — `{ Key, Version, Name, MaxRoundsPerRound, Nodes[], Edges[], Inputs[] }`.
- `WorkflowNode` — `{ Id, Kind, AgentKey?, AgentVersion?, Script?, OutputPorts[], LayoutX, LayoutY, SubflowKey?, SubflowVersion?, ReviewMaxRounds?, LoopDecision? }`.
- `WorkflowEdge` — `{ FromNodeId, FromPort, ToNodeId, ToPort, RotatesRound, SortOrder }`.
- `WorkflowInput` — `{ Key, DisplayName, Kind, Required, DefaultValueJson?, Description?, Ordinal }`.
- `WorkflowSagaStateEntity` — carries `CurrentNodeId`, `CurrentAgentKey`, `EscalatedFromNodeId`, `InputsJson`, and append-only `DecisionHistoryJson` + `LogicEvaluationHistoryJson`.
- `AgentInvokeRequested` — `{ TraceId, RoundId, WorkflowKey, WorkflowVersion, NodeId, AgentKey, AgentVersion, InputRef, ContextInputs, RetryContext?, GlobalContext?, ReviewRound?, ReviewMaxRounds? }`.
- `AgentInvocationCompleted` — `{ TraceId, RoundId, FromNodeId, AgentKey, AgentVersion, OutputPortName, OutputRef, Decision, DecisionPayload, Duration, TokenUsage }`.
- `SubflowInvokeRequested` — `{ ParentTraceId, ParentNodeId, ParentRoundId, ChildTraceId, SubflowKey, SubflowVersion, InputRef, SharedContext, Depth, ReviewRound?, ReviewMaxRounds?, LoopDecision? }`.
- `SubflowCompleted` — `{ ParentTraceId, ParentNodeId, ParentRoundId, ChildTraceId, OutputPortName, OutputRef, SharedContext, Decision?, ReviewRound?, TerminalPort? }`.
- `WorkflowSagaStateEntity` (subflow fields) — `ParentTraceId?`, `ParentNodeId?`, `ParentRoundId?`, `SubflowDepth`, `GlobalInputsJson?`, `ParentReviewRound?`, `ParentReviewMaxRounds?`, `LastEffectivePort?`, `ParentLoopDecision?`.

## Useful endpoints

- `GET /api/workflows` — summaries for each workflow's latest version.
- `GET /api/workflows/{key}` — latest version detail.
- `GET /api/workflows/{key}/{version}` — specific version detail.
- `POST /api/workflows` — create (rejects if `key` exists).
- `PUT /api/workflows/{key}` — append a new version.
- `POST /api/workflows/validate-script` — syntax-check a script (used by Logic nodes and by agent-attached routing scripts — same endpoint).
- `POST /api/traces` — launch a trace; accepts `workflowKey`, optional `workflowVersion`, `input` (text that becomes the Start agent's input artifact), and `inputs` (workflow-level context map).
