# CodeFlow

CodeFlow is a multi-agent workflow platform for authoring, running, and reviewing agent-driven work. A .NET API and worker drive an event-driven orchestration saga; persistence stores versioned configuration and trace history; an Angular UI hosts the workflow editor, agent editor, trace viewer, HITL queue, and admin pages. Workflows are data — JSON definitions referencing versioned agent configs, prompt partials, MCP servers, agent roles, and skill grants — not code in this repo.

The current tree includes:

- versioned agent configuration with in-place forking from the workflow editor
- a visual workflow editor with Agent, Logic, HITL, Subflow, ReviewLoop, Transform, and Swarm nodes
- user-defined output ports plus an implicit `Failed` port on every node
- per-trace-tree workflow variables (`workflow.*`) and per-trace context (`context.*`)
- input and output routing scripts on every Agent/HITL/Start node
- Scriban prompt templates with reusable `@codeflow/*` partial pins
- pluggable save-time validation, dataflow analysis, and dry-run / fixture-based testing
- code-aware workflows with per-trace working directories, repo cloning, and `vcs.*` host tools
- trace submission, streaming trace detail, and HITL queue screens
- admin pages for MCP servers, skills, agent roles, git hosts, and LLM providers
- a Docker-based local stack for the API, worker, UI, MariaDB, RabbitMQ, and Aspire dashboard

## Solution layout

- `CodeFlow.Api` — ASP.NET Core API, HTTP endpoints, validation pipeline, workflow templates, cascade-bump
- `CodeFlow.Worker` — background worker for orchestration and long-running processing
- `CodeFlow.Host` — shared hosting, transport, observability, workspace, and dead-letter wiring
- `CodeFlow.Orchestration` — workflow saga state machine, dataflow analyzer, dry-run executor
- `CodeFlow.Runtime` — agent runtime, model clients, MCP integration, workspace tooling, Scriban renderer
- `CodeFlow.Persistence` — EF Core data model, repositories, migrations, artifact storage
- `CodeFlow.Contracts` — shared contracts and message types
- `codeflow-ui` — Angular 20 frontend
- `docs` — feature, design, and operator references
- `starter_workflows` — packaged starter workflow definitions
- `workflows` — first-party workflow library packages

## Prerequisites

- .NET 10 SDK
- Node.js with npm
- Docker Desktop or compatible Docker runtime

## Quick start with Docker

From the repo root:

```bash
docker network create mcp-shared
docker compose up --build
```

Main local endpoints:

- UI: [http://localhost:4200](http://localhost:4200)
- API: [http://localhost:5080](http://localhost:5080)
- RabbitMQ management: [http://localhost:15673](http://localhost:15673)
- Aspire dashboard: [http://localhost:18888](http://localhost:18888)

Default local infrastructure credentials:

- MariaDB: `codeflow` / `codeflow_dev` on `127.0.0.1:3306`
- RabbitMQ: `codeflow` / `codeflow_dev` on `127.0.0.1:5673`, vhost `codeflow`

Optional model/secrets configuration can be provided through environment variables. See [dot_env_sample.txt](./dot_env_sample.txt).

To stop the stack:

```bash
docker compose down
```

To stop it and remove Docker-managed volumes:

```bash
docker compose down -v
```

## Local development

### Backend

The API applies database migrations on startup and, by default, listens on `http://localhost:5080`.

```bash
dotnet restore CodeFlow.slnx
dotnet run --project CodeFlow.Api
dotnet run --project CodeFlow.Worker
```

### Frontend

The Angular app proxies `/api` to `http://localhost:5080`.

```bash
cd codeflow-ui
npm install
npm start
```

## Testing

Run the .NET test projects:

```bash
dotnet test CodeFlow.slnx
```

Run frontend checks from `codeflow-ui`:

```bash
npm run typecheck
npm run build
```

## Core concepts

CodeFlow is built around a small set of primitives. Most of the recent work has been about making these primitives easier to compose, easier to debug, and harder to misuse.

### Agents, workflows, and traces

- **Agents** are versioned configurations: model/provider, system prompt, prompt template, declared outputs (port names + optional template), tool/skill grants, and partial pins.
- **Workflows** are versioned directed graphs that route work between Start, Agent, Logic, HITL, Subflow, ReviewLoop, Transform, and Swarm nodes.
- **Traces** capture workflow execution: artifacts, decisions, evaluations, HITL pauses, and per-trace working directories.

### Node kinds

| Kind | What it does |
|---|---|
| Start | First node of a workflow. Runs an agent like any other Agent node, but is also the entry point that scripts and templates can target with `{{ workflow.traceId }}` etc. |
| Agent | LLM call. Receives an artifact, runs the agent, emits an artifact and a port name. |
| Logic | Pure JS routing. Picks an outbound port via `setNodePath()`. No agent, no LLM. |
| HITL | Halts the trace and surfaces a form to a human. The form's `outputTemplate` is rendered server-side per submission. |
| Subflow | Calls a child workflow as a reusable unit. Inherits the child's terminal port set. |
| ReviewLoop | Specialized subflow with a bounded produce-review-revise loop. Synthesizes `Exhausted`; iterates while the child's terminal port equals the configured `LoopDecision` (default `"Rejected"`). |
| Transform | Deterministic Scriban template that rewrites the artifact mid-traversal. No agent, no LLM. `outputType: "string" \| "json"`. |
| Swarm | Fans out to N contributor agents, then a synthesizer agent emits the terminal output. Two protocols ship: `Sequential` (n+1 LLM calls; each contributor sees prior drafts) and `Coordinator` (n+2 LLM calls; coordinator plans + assigns roles, n workers run in parallel). Non-replayable. |

See [docs/workflows.md](./docs/workflows.md) for the full model, [docs/transform-node.md](./docs/transform-node.md) for the Scriban-render-only node, and [docs/swarm-node.md](./docs/swarm-node.md) for the Swarm protocol contract.

### User-defined ports

Output ports are author-named strings. Every node has an **implicit `Failed`** port that's always wirable but never declared — runtime errors and agent-submitted Failed decisions route through it. Subflow / ReviewLoop nodes inherit their child workflow's terminal port set; agent nodes are validated against the pinned agent's declared outputs. See [docs/port-model.md](./docs/port-model.md).

### Scopes: `input`, `output`, `context`, `workflow`

- `input.*` — the artifact arriving at the node (parsed if JSON).
- `output.*` — only available in **output scripts**: the agent's submission with `output.decision` and `output.decisionPayload` attached.
- `context.*` — per-trace bag, local to the current saga. Resets on subflow boundaries.
- `workflow.*` — per-trace-tree bag (the renamed-from-`global` scope). Survives subflow / ReviewLoop boundaries; child writes shallow-merge back into the parent on completion.

The `__loop` namespace under `workflow.*` is reserved (e.g. `__loop.rejectionHistory`); see [docs/features/rejection-history.md](./docs/features/rejection-history.md).

### Input and output scripts

Every Agent / HITL / Start node carries up to two optional scripts:

- **Input script** runs **before** the node sees its input. Sees `input`. Optionally calls `setInput(text)` to rewrite the inbound artifact. Up to 1 MiB.
- **Output script** runs **after** the agent completes. Sees `output`. Calls `setNodePath('PortName')` (required) and optionally `setOutput(text)` to rewrite the outbound artifact. Up to 1 MiB.

Logic nodes use a single script that must call `setNodePath` and may not call `setInput`/`setOutput`. See [docs/workflows.md](./docs/workflows.md).

### Subflows and ReviewLoops

- Workflows compose: a Subflow node calls a child workflow and resumes routing from the matching terminal port name on the parent. Recursion cap is depth 3.
- ReviewLoops add bounded iteration: `MaxRounds` (1–10), `LoopDecision`, optional built-in rejection-history accumulation, automatic `@codeflow/last-round-reminder` injection, and template variables `round` / `maxRounds` / `isLastRound`.
- HITL inside a child surfaces on every ancestor trace's `pendingHitl` list.

See [docs/subflows.md](./docs/subflows.md) and [docs/review-loop.md](./docs/review-loop.md).

### Code-aware workflows

When a `GitHostSettings.WorkingDirectoryRoot` is configured, the API mints a per-trace working directory (`<root>/<traceId>`) and seeds it onto `workflow.workDir`. Agents with the seeded `code-worker` role get path-jailed `read_file` / `apply_patch` / `run_command` tools and `vcs.open_pr` / `vcs.get_repo` against the configured Git host. Repos[] input convention: pass `{ "repositories": [{ "url": "..." }] }` and the runtime clones into the workdir. A background sweep cleans up after `WorkingDirectoryMaxAgeDays` (default 14). See [docs/code-aware-workflows.md](./docs/code-aware-workflows.md).

### Prompt templates

Scriban 7.1.0 powers all prompt rendering. Legacy `{{ name }}` placeholders still work; conditionals, loops, filters, and `{{ include "@codeflow/<name>" "@vN" }}` partial pins are supported. Sandboxed: 50 ms timeout, no `include` / `import` outside the partial registry, 1 MB output cap. See [docs/prompt-templates.md](./docs/prompt-templates.md).

## Workflow Authoring DX

Authoring CodeFlow workflows used to demand memorizing the docs. The Workflow Authoring DX epic addressed this in code rather than prose: validators that surface mistakes at save time, primitives that absorb recurring boilerplate, scaffolding templates for common shapes, editor IntelliSense for scripts and prompts, and a dry-run harness for fixture-driven testing.

### Save-time validation

A pluggable [`WorkflowValidationPipeline`](./CodeFlow.Api/Validation) runs on every save, surfaced as `POST /api/workflows/validate`. Errors block save; warnings inform.

| Rule | Severity | What it catches |
|---|---|---|
| `port-coupling` | Error / Warning | Wired-but-undeclared ports vs. declared-but-unwired ports for Start / Agent / HITL nodes against the pinned agent. |
| `missing-role` | Error / Warning | Agent has no role granted; capability mention in prompt (`read_file`, `apply_patch`, `run_command`, `vcs.*`, `mcp:*`) escalates to Error. |
| `backedge` | Warning | Cycles in the routed graph. Dismiss per-edge with `intentionalBackedge: true`. |
| `prompt-lint` | Warning | Forbidden phrases in agent prompts (`default to Rejected`, `keep iterating until`, etc.). |
| `protected-variable-target` | Error | Mirror or port-replacement targets writing to reserved namespaces (`__loop.*`, etc.). |
| `workflow-vars-declaration` | Warning | Prompts/scripts read or write workflow variables not in the workflow's declared `WorkflowVarsReads` / `WorkflowVarsWrites`. |
| `start-node-advisory` | Info | Start node sanity advisories. |

Package export accumulates **every** missing reference (`MissingPackageReference` with kind + key + version + origin) in one 422 response so editors can render click-to-jump anchors. See [docs/authoring-workflows.md](./docs/authoring-workflows.md).

### Pattern-to-feature primitives

Recurring author boilerplate has been replaced with declarative configuration:

- **`@codeflow/*` partials.** Five seeded prompt partials — `reviewer-base`, `producer-base`, `last-round-reminder`, `no-metadata-sections`, `write-before-submit` — pinned by version on agent configs. The `@codeflow/last-round-reminder` partial is **auto-injected** in the final round of a ReviewLoop unless the agent opts out or already pins it. See [docs/features/prompt-partials.md](./docs/features/prompt-partials.md).
- **Built-in rejection history.** `WorkflowNode.RejectionHistoryConfig = { Enabled, MaxBytes, Format }` on a ReviewLoop accumulates each round's rejection into `workflow.__loop.rejectionHistory`, exposed un-prefixed as `{{ rejectionHistory }}` to the child's templates. UTF-8-safe trim, idempotent on redelivery. See [docs/features/rejection-history.md](./docs/features/rejection-history.md).
- **Mirror output to workflow variable.** `WorkflowNode.MirrorOutputToWorkflowVar = "key"` copies the agent's submitted artifact into `workflow.key` **before** the output script runs. Replaces the Pattern-1 capture script. See [docs/features/mirror-output-to-workflow-var.md](./docs/features/mirror-output-to-workflow-var.md).
- **Per-port artifact replacement.** `WorkflowNode.OutputPortReplacements = { "PortName": "workflowVarKey" }` swaps the outbound artifact for a workflow variable on a specific port — applied **after** the output script, so port binding wins over `setOutput()`. Replaces the Pattern-2 replace-on-port script. See [docs/features/replace-artifact-from-workflow-var.md](./docs/features/replace-artifact-from-workflow-var.md).

### Workflow templates

`POST /api/workflow-templates/{id}/materialize` mints a parameterized starter into the database with collision pre-flight (no half-materialized state on conflict):

| Template | What it lays down |
|---|---|
| `empty` | Single Start agent stub. |
| `review-loop-pair` | Trigger + producer (`@codeflow/producer-base` v1) + reviewer (`@codeflow/reviewer-base` v1) + inner workflow + outer ReviewLoop with `RejectionHistory.Enabled=true`. |
| `hitl-approval-gate` | Trigger + passthrough HITL form (`outputTemplate: "{{ input }}"`) with Approved + Cancelled ports. |
| `setup-loop-finalize` | Setup agent (with `inputScript` TODO comments) → ReviewLoop → on Exhausted route to HITL escalation. |
| `lifecycle-wrapper` | Trigger → Subflow → HITL gate → Subflow → HITL gate → Subflow (3 phases). |

See [docs/features/workflow-templates.md](./docs/features/workflow-templates.md).

### Editor IntelliSense and tooling

The Monaco-based script and prompt editor (`MonacoScriptEditorComponent`) underpins every script slot and prompt template field across the workflow and agent editors. It carries:

- **Per-slot ambient `.d.ts`.** Workflow-canvas computes a typed lib per Monaco editor: `output` / `setOutput` / `setNodePath` only in output-script slots; `input` / `setInput` only in input-script slots; loop bindings (`round`, `maxRounds`, `isLastRound`) gated on the dataflow snapshot's `loopBindings`; `workflow` and `context` narrowed to detected keys via literal property declarations + `[key: string]: unknown` index signature.
- **Snippet library.** Seven versioned JS snippets (`pattern-1`, `pattern-2`, `accumulate-rejection-history`, `set-node-path-rotate`, etc.) gated to slots whose ambient libs include every referenced symbol. Legacy snippets render with `(legacy)` and the documentation tile names the Phase-3 built-in feature that supersedes them.
- **Prompt-template autocomplete.** `plaintext` completion provider for agent `promptTemplate` and HITL `outputTemplate`: 5 stock `@codeflow/*` partial includes, 3 loop bindings, `input`, and `workflow.` / `context.` placeholders. Cursor-aware: only fires inside `{{ ... }}` Scriban tags.
- **Cascade-bump assistant.** `POST /api/workflows/cascade-bump/plan` returns a BFS-ordered plan of every workflow whose latest version pins a chosen agent or subflow at a chosen `FromVersion`. `/apply` re-plans against the live DB and creates new workflow versions sequentially, rewriting agent and subflow pins to the actually-created versions.
- **Package preview.** Export now opens an in-app preview card (V8 manifest as a collapsible dependency tree, per-entity byte estimates, total size, "Self-contained" chip) before download. Missing-references render red with the export disabled.

### Visibility

- **Data-flow inspector** — for any selected node: workflow / context variables in scope (Definite vs Conditional pill, clickable source-node links), expected input artifact, loop bindings, and auto-injected partials. Fed by `IWorkflowDataflowAnalyzer` (`GET /api/workflows/{key}/{version}/dataflow`), an Acornima-backed JS AST walker that propagates `setWorkflow` / `setContext` / `setInput` / `setOutput` writes through the predecessor graph.
- **Port-coupling visualizer** — inspector port list flags `stale` (wired but undeclared by the pinned agent) and `missing` (declared but absent on the node), with a one-click "Sync from agent" button.
- **Subflow port preview** — Subflow / ReviewLoop pickers render `{key} (vN) → port1, port2, Failed` for every candidate.
- **Backedge highlighting** — frontend DFS port of the `backedge` rule renders dashed-amber edges with cycle members in a tooltip; "Yes, intentional — dismiss this warning" toggle on the inspector flips `IntentionalBackedge` and round-trips through save.

### Dry-run testing and replay-with-edit

`POST /api/workflows/{key}/dry-run` runs a workflow against a `WorkflowFixture` (or inline mocks) without invoking the LLM. The `DryRunExecutor` walks the graph node-by-node and reuses `LogicNodeScriptHost` directly so JS sandbox semantics match production. Saga-parity coverage: input/output scripts, decision-output Scriban templates, P3/P4/P5 built-ins, HITL form rendering, retry-context handoff. Fixture CRUD lives under `/api/workflows/{key}/fixtures`; the UI route `/workflows/:key/dry-run` renders state, terminal port, HITL form payload, final artifact, workflow + context vars, and the full event timeline.

**Replay-with-edit** (its own epic, design doc landed) reuses the dry-run executor in substitution-only mode: lift recorded `DecisionRecord` rows into mocks, edit specific positions, and walk the past trace deterministically without re-running the LLM. Lengthening edits require explicit `additionalMocks[]`; backend endpoint and UI panels are next. See [docs/replay-with-edit.md](./docs/replay-with-edit.md).

### In-place agent edit

Right-click a workflow node → "Edit agent" opens a modal that **forks** the agent into a workflow-scoped copy (key prefix `__fork_<shortGuid>`, hidden from the agent list). Save updates the fork; "Publish back" creates a new version on the original key (or a fresh agent) and re-pins the workflow node to the published target. Drift detection compares the fork's `ForkedFromVersion` to the original's current latest; publishing is gated behind `acknowledgeDrift` when they differ. See [docs/agent-in-place-edit.md](./docs/agent-in-place-edit.md).

## Assistant

CodeFlow ships a conversational assistant on every page. It's a CodeFlow domain expert: it knows the platform's primitives, can query the live system through tool calls, can author + run workflows on your behalf, and can diagnose failed traces and propose replay-with-edit substitutions. Every mutating action is gated by an in-chat confirmation chip — the model never silently changes state.

The assistant lives in two surfaces — a chat-first **homepage** (`/`) for open-ended conversations, and a collapsible **sidebar** that opens on every other page scoped to the current entity (`/traces/{id}`, `/workflows/{key}`, etc.) so returning to the same page resumes the prior debugging thread. Conversations persist per `(user, scope)` and stream over Server-Sent Events. Authenticated callers get full tool access; anonymous visitors get a cookie-backed demo-mode chat with knowledge-only access (system-prompt answers, no live queries).

### What the assistant can do

#### Knowledge

The assistant's curated system prompt covers every authoring + runtime concept in this README — ports, scripting, subflows, ReviewLoops, swarms, transforms, HITL, traces, replay-with-edit, drift detection, token tracking, in-place agent edit, code-aware workflows, working-directory model — so concept questions are answered without a tool call.

#### Live discovery and inspection (read-only tools)

| Tool | What it returns |
|---|---|
| `list_workflows` / `get_workflow` / `list_workflow_versions` | Library browse + per-version detail |
| `list_agents` / `get_agent` / `list_agent_versions` | Agent registry + version history |
| `list_agent_roles` | Roles + tool/skill grants |
| `find_workflows_using_agent` | Reverse lookup — which workflows pin agent X |
| `search_prompts` | Substring search across agent prompts + Scriban templates |
| `list_traces` / `get_trace` | Trace browse + saga summary |
| `get_trace_timeline` | Decision + logic-evaluation history with port names |
| `get_trace_token_usage` | Per-call / per-node / per-scope rollups |
| `get_node_io` | Input + output artifacts for a specific node invocation |

The trace inspector tools share the same aggregation code path that powers the per-trace UI panel, so "what does the inspector see for this trace" and "what did the assistant tell me about this trace" never diverge.

#### Authoring + execution (chip-gated mutations)

| Tool | Confirmation chip → action |
|---|---|
| `save_workflow_package` | "Save to library" → POST `/api/workflows/package/apply` |
| `run_workflow` | "Run" → POST `/api/traces` |
| `propose_replay_with_edit` | "Replay" → POST `/api/traces/{id}/replay` (DryRunExecutor v4) |

Each mutating tool returns a `preview_ok` verdict only — no side effects. The chat panel attaches a confirmation chip carrying the validated payload; only the user clicking the chip triggers the actual mutation, and only under the user's own auth policy (`WorkflowsWrite` / `TracesWrite` / `TracesRead` respectively). Unauthenticated demo-mode users can still draft packages + propose replays, but the chips never reach a real endpoint.

For drafting, the assistant emits a complete workflow package — schema-version-pinned, self-contained (every referenced agent, role, MCP server, skill, and subflow), recursively resolved — in a single fenced JSON block (` ```cf-workflow-package ` language hint). The chat UI parses the block, surfaces a collapsible preview with byte estimates and a self-containment chip, and only saves on chip confirmation.

For replay-with-edit, the assistant proposes substitution edits keyed by `(agentKey, ordinal)` against the trace's recorded decisions. The tool validates each edit against the saga subtree's recorded decisions and the substitution kinds DryRunExecutor supports (Swarm nodes are explicitly non-replayable; synthetic subflow markers are rejected with a clear message). On confirm, the chat panel POSTs the edits to the existing replay endpoint; the success banner deep-links to the trace inspector's Replay-with-Edit panel for further refinement.

#### Diagnosis

`diagnose_trace` composes saga + decisions + logic evaluations + token usage into a structured verdict with anomaly heuristics applied server-side (`long_duration`, `token_spike`, `logic_failure`). The output names the failing node, cites evidence by anomaly id, and suggests next actions as deep-linked URLs (trace inspector, agent editor, replay-with-edit candidates). Works on completed traces too — those report empty `failingNodes` but may still surface anomalies.

#### Page-aware context

When the sidebar opens on a trace, workflow, or node, the chat backend injects a `<current-page-context>` system message describing the active entity (`{ kind, route, entityType, entityId, selectedNodeId, selectedScriptSlot }`). The assistant resolves "this trace", "this node", "this script" implicitly without asking — say "why did this fail?" on a trace page and `diagnose_trace` runs against the active id.

The same context drives **suggestion chips** above the composer: contextually-relevant quick prompts ("Explain what this node does", "Help me write this output script", "Suggest a Scriban template for…") that the user can click instead of typing.

### Homepage rail

The homepage's right-side rail shows three live sections plus an assistant-token chip:

- **Resume conversations** — your recent threads (homepage + entity-scoped) ordered by most-recent activity, with first-user-message previews.
- **Recent traces** — last N traces with quick-link state badges.
- **Recently used workflows** — workflows ordered by most recent saga activity (no separate "pinned" concept; recency is implicit).
- **Assistant tokens · today** — input / output rollup for the user's assistant conversations, plus an all-time call count.

The trace token panel (`GET /api/traces/{id}/token-usage`) labels assistant-conversation synthetic traces as **"Assistant token usage"** with an `Assistant` chip, distinguishing them from workflow saga runs in the same UI.

### Implementation surface

- **Backend**: `CodeFlow.Api/Assistant/` hosts the chat loop (`CodeFlowAssistant`), tool dispatcher (`AssistantToolDispatcher`), provider mappers (`AnthropicToolMapper`, `OpenAiToolMapper`), and the `IAssistantTool` registry. Tools are scoped per-request and registered via single-line DI; adding a new tool is one `services.AddScoped<IAssistantTool, FooTool>()` line.
- **Persistence**: `AssistantConversationEntity` + `AssistantMessageEntity` hold conversations and the message log. Each conversation owns a `SyntheticTraceId` so token usage flows through the same `TokenUsageRecord` table as workflow runs and shows up in the existing token panels.
- **LLM backend**: shared with the rest of the platform via the CodeGraph LLM Configuration admin (anthropic / openai / lmstudio). No parallel assistant-specific provider config.
- **Frontend**: `codeflow-ui/src/app/ui/chat/` ships an embeddable `cf-chat-panel` mounted both as the homepage main pane and as the page sidebar. SSE stream parsing (`assistant-stream.ts`), tool-call rendering (`chat-tool-call.component.ts`), confirmation chips (`chat-confirmation-chip.component.ts`), and the homepage rail (`pages/home/home-rail.component.ts`) live in their own focused files.

### Endpoints

- `POST /api/assistant/conversations` — get-or-create a conversation by `(user, scope)`.
- `GET /api/assistant/conversations` — recent conversations for the caller (HAA-14 resume-list rail).
- `GET /api/assistant/conversations/{id}` — conversation + message history.
- `POST /api/assistant/conversations/{id}/messages` — SSE-streaming chat turn (deltas, tool calls, tool results, token usage, terminal `done` event).
- `GET /api/assistant/token-usage/summary` — today / all-time / per-conversation token rollup for the rail's assistant-token chip.

## Production deployment

CodeFlow is deployed to `https://codeflow.trefry.net` on a Linode host fronted by host-managed Caddy, authenticated against Keycloak at `https://identity.trefry.net`, and connected to the shared MariaDB on `trefry-network` and the shared RabbitMQ at `mqapps.trefry.net`.

- [Production deployment + Caddy + GitHub Actions](./docs/deployment.md)
- [Keycloak realm + client configuration](./docs/deployment-keycloak.md)

## Docs index

### Workflow model

- [Workflow model and editor behavior](./docs/workflows.md)
- [Authoring workflows (DX, validators, primitives)](./docs/authoring-workflows.md)
- [Port model — user-defined ports + implicit Failed](./docs/port-model.md)
- [Subflows and workflow composition](./docs/subflows.md)
- [Review loop design](./docs/review-loop.md)
- [Transform node](./docs/transform-node.md)
- [Swarm node — Sequential + Coordinator protocols](./docs/swarm-node.md)
- [Decision-output templates](./docs/decision-output-templates.md)
- [Prompt templates (Scriban)](./docs/prompt-templates.md)
- [Code-aware workflows](./docs/code-aware-workflows.md)

### Pattern-to-feature primitives

- [Built-in rejection history (P3)](./docs/features/rejection-history.md)
- [Mirror output to workflow variable (P4)](./docs/features/mirror-output-to-workflow-var.md)
- [Replace artifact from workflow variable (P5)](./docs/features/replace-artifact-from-workflow-var.md)
- [Prompt partials (P1 / F3 + P2 auto-injection)](./docs/features/prompt-partials.md)
- [Workflow templates (S3-S7)](./docs/features/workflow-templates.md)

### Tooling

- [Agent in-place editing](./docs/agent-in-place-edit.md)
- [Replay-with-edit on past traces](./docs/replay-with-edit.md)

### Assistant

- See the [Assistant](#assistant) section above for the full capability surface — tools, confirmation-chip flow, page-aware context, homepage rail, and endpoints.

### Operations

- [Local integration stack notes](./docs/local-integration-stack.md)
- [Production deployment](./docs/deployment.md)
- [Keycloak realm + client](./docs/deployment-keycloak.md)
- [Kickoff implementation plan (historical)](./docs/kickoff-implementation-plan.md)

## Notes

- The root `docker-compose.yml` is the most accurate source for local container topology and ports.
- The UI has its own focused notes in [codeflow-ui/README.md](./codeflow-ui/README.md).
