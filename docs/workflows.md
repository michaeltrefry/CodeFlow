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

`Kind` is `Text` or `Json`. At trace launch, the UI renders a form from the schema and posts concrete values on `CreateTraceRequest.inputs`. The resolved map is frozen onto the saga state for the duration of the run; every `AgentInvokeRequested` published during the trace includes it as `ContextInputs`, and the Logic-node script host exposes it as a frozen `context` object.

Inputs are immutable once a trace is launched. Nodes cannot mutate them.

## Logic nodes

Logic nodes execute JavaScript inside a sandboxed Jint engine. They are pure routers — they do not produce new output. The script receives:

- `input` — parsed JSON payload from the upstream node's output. For agent nodes, this is usually the decision payload; for plain-text agent output, the runtime wraps it as `{ "text": "…" }`.
- `context` — frozen object containing the workflow's resolved inputs. Mutation attempts throw in strict mode.
- `setNodePath(portName)` — picks the outgoing edge. Must be called at least once. Must reference a port declared on the node.
- `log(message)` — appends to the trace log for the current evaluation.

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
- `POST /api/workflows/validate-script` — syntax-check a Logic node script.
- `POST /api/traces` — launch a trace; accepts `workflowKey`, optional `workflowVersion`, `input` (text that becomes the Start agent's input artifact), and `inputs` (workflow-level context map).
