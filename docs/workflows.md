# Workflows

Workflows describe how a trace moves between agents and logic checks to get a job done. The UI is a ComfyUI-style drag-and-drop canvas at `/workflows/:key/edit`; the underlying data model is a directed graph of **nodes** connected by **edges** that reference named **output ports**.

## Node kinds

Every workflow is composed of nodes. The canvas palette exposes five kinds:

| Kind | Purpose | Has agent | Has script |
|---|---|---|---|
| `Start` | Single entry point for any trace. Exactly one per workflow. | Yes | No |
| `Agent` | Dispatches an agent via `AgentInvokeRequested`. | Yes | No |
| `Hitl` | Dispatches a human-in-the-loop agent that pauses the saga until a human submits a decision via `/api/traces/:id/hitl-decision`. | Yes | No |
| `Logic` | In-process JavaScript router. Does not dispatch an agent; evaluates a script that picks an output port. | No | Yes |
| `Escalation` | Fallback when a round count overflows. At most one per workflow. | Yes | No |

Every node has a stable `Id` (GUID) that persists across saves, a set of named **output ports**, and layout coordinates `(LayoutX, LayoutY)`.

## Edges and ports

An edge is `(FromNodeId, FromPort) → (ToNodeId, ToPort)` plus a `RotatesRound` flag and a `SortOrder`. Ports are strings — for agent nodes the port name equals the returned `AgentDecisionKind` (`Completed`, `Approved`, `ApprovedWithActions`, `Rejected`, `Failed`). Logic nodes declare their ports explicitly.

**Connection rules enforced by the editor:**

- At most one outgoing edge per output port.
- Fan-in is allowed on input ports.
- Escalation nodes have no outgoing edges.
- The `ToPort` defaults to `"in"` — every non-Start node implicitly has one input port.

If the runtime follows an edge that would push the round count past `MaxRoundsPerRound` (and the edge does not rotate), it routes to the Escalation node instead of the intended target. If no Escalation node is configured, the trace terminates as `Failed`.

## Workflow inputs (global context)

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

Initial inputs are seeded once at trace launch. Agent and HITL nodes cannot mutate context. Logic nodes can write top-level keys via `setContext` (see below) so loops and multi-turn flows can accumulate state across iterations.

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
2. If the node has a non-empty `Script`, reads the agent's output artifact as JSON, attaches `input.decision` (the AgentDecisionKind name: `Completed`, `Approved`, `ApprovedWithActions`, `Rejected`, `Failed`) and `input.decisionPayload` (the raw decision payload or `null`), and evaluates the script with the workflow's current `context`.
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

## Workflow versions

Workflows are immutable by version. Every Save creates a new `(key, version)` row; in-flight traces continue running against the version they launched with. The editor opens the latest version by default; older versions remain available via `/api/workflows/{key}/versions` and the detail page's version picker.

## Runtime shape

At a glance:

- `Workflow` — `{ Key, Version, Name, MaxRoundsPerRound, Nodes[], Edges[], Inputs[] }`.
- `WorkflowNode` — `{ Id, Kind, AgentKey?, AgentVersion?, Script?, OutputPorts[], LayoutX, LayoutY }`.
- `WorkflowEdge` — `{ FromNodeId, FromPort, ToNodeId, ToPort, RotatesRound, SortOrder }`.
- `WorkflowInput` — `{ Key, DisplayName, Kind, Required, DefaultValueJson?, Description?, Ordinal }`.
- `WorkflowSagaStateEntity` — carries `CurrentNodeId`, `CurrentAgentKey`, `EscalatedFromNodeId`, `InputsJson`, and append-only `DecisionHistoryJson` + `LogicEvaluationHistoryJson`.
- `AgentInvokeRequested` — `{ TraceId, RoundId, WorkflowKey, WorkflowVersion, NodeId, AgentKey, AgentVersion, InputRef, ContextInputs, RetryContext? }`.
- `AgentInvocationCompleted` — `{ TraceId, RoundId, FromNodeId, AgentKey, AgentVersion, OutputPortName, OutputRef, Decision, DecisionPayload, Duration, TokenUsage }`.

## Useful endpoints

- `GET /api/workflows` — summaries for each workflow's latest version.
- `GET /api/workflows/{key}` — latest version detail.
- `GET /api/workflows/{key}/{version}` — specific version detail.
- `POST /api/workflows` — create (rejects if `key` exists).
- `PUT /api/workflows/{key}` — append a new version.
- `POST /api/workflows/validate-script` — syntax-check a script (used by Logic nodes and by agent-attached routing scripts — same endpoint).
- `POST /api/traces` — launch a trace; accepts `workflowKey`, optional `workflowVersion`, `input` (text that becomes the Start agent's input artifact), and `inputs` (workflow-level context map).
