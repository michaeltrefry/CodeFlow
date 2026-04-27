# Authoring Workflows in CodeFlow

A practical guide for designing, building, and shipping CodeFlow workflows via the UI. Captures hard-won lessons from authoring the PRD intake, architect/reviewer plan, and dev-loop workflows.

> **About the `workflow` bag.** Each top-level trace owns a single `workflow` bag — a per-trace-tree key/value store that propagates *down* into subflows and ReviewLoops via copy-on-fork: children get a snapshot at the moment they spawn, and at child completion the child's final bag merges back into the parent. The bag is **not** process-wide and **not** shared across separate traces. Read in templates as `{{ workflow.X }}`, in scripts as `workflow.X`, written via `setWorkflow(...)`.

## Quick links

The Workflow Authoring DX epic added several built-in features so you can do less yourself. If you're new to the platform, read this whole doc once. If you're updating an existing workflow, jump straight to:

- [`features/rejection-history.md`](features/rejection-history.md) — built-in P3 accumulator (no more hand-rolled "round N" scripts).
- [`features/mirror-output-to-workflow-var.md`](features/mirror-output-to-workflow-var.md) — P4 checkbox replacing Pattern-1 capture scripts.
- [`features/replace-artifact-from-workflow-var.md`](features/replace-artifact-from-workflow-var.md) — P5 per-port config replacing Pattern-2 setOutput scripts.
- [`features/prompt-partials.md`](features/prompt-partials.md) — `@codeflow/*` partial library (P1 / F3).
- [`features/workflow-templates.md`](features/workflow-templates.md) — S3 framework + S4 ReviewLoop pair scaffold.

## Mental model

A **workflow** is a graph of nodes connected by edges. When a user submits an input, CodeFlow creates a **trace** that walks the graph, invoking agents and pausing at HITL forms. The runtime state of an in-flight trace is a **saga** — durable, resumable, restartable across server restarts.

Five things every author needs to know:

1. **Nodes have output ports.** Each port is a named exit. Edges connect a `(node, port)` pair to another node's input. A node can have many ports; you wire each one separately.
2. **Every node has an implicit `Failed` port.** It's not declared and isn't shown unless you wire to it. If wired, runtime errors route to that recovery path. If unwired, the trace just terminates.
3. **Workflow vs context.** A trace has a `workflow` bag (shared across the entire trace, including all subflows) and a `context` bag (local to one workflow's saga). When you embed a subflow, the child sees the parent's workflow bag as a snapshot but starts with empty context. **Workflow vars propagate; context doesn't.**
4. **Subflow nodes inherit ports from their child.** The output ports of a Subflow node aren't authored — they come from the child workflow's terminal ports (i.e., ports with no outgoing edge in the child).
5. **Versions are immutable.** Once a workflow or agent is saved at v2, you can never edit v2 again. Edits create v3. Workflow nodes pin a specific agent version, so the running graph is deterministic over the version's lifetime.

## Node kinds — when to use each

| Kind | Use it for | Notes |
|------|-----------|-------|
| **Start** | The first node of every workflow. Must reference an agent. | Exactly one per workflow. |
| **Agent** | Invokes an LLM agent. | Most common node. |
| **Hitl** | Pauses the trace and shows a form to a human. The human's response becomes the artifact. | Use for approval gates, interview answers, end-of-loop escalation. |
| **Logic** | Pure JavaScript — no LLM call. Reads context/workflow/input, picks an output port. | Cheap routing decisions you don't want to spend tokens on. |
| **Subflow** | Embeds another workflow as a single node. Inherits ports from the child. | Composition primitive. |
| **ReviewLoop** | Same as Subflow but iterates the child until it exits on a non-`loopDecision` port (or hits `reviewMaxRounds`). | The only loop primitive. |

## Agent editor — what each field actually does

When you open the Agent editor (right-click a node → Edit, or via the Agents page), you set:

- **System prompt.** The LLM's persona, instructions, and rules. Things that don't change per invocation.
- **Prompt template.** Renders into the user message at invocation time, using Scriban (`{{ ... }}`). Variables include `{{ input }}` (the upstream artifact body), `{{ context.foo }}`, `{{ workflow.foo }}`, plus loop bindings inside ReviewLoops: `{{ round }}`, `{{ maxRounds }}`, `{{ isLastRound }}`. Inside ReviewLoops with rejection-history enabled, `{{ rejectionHistory }}` is available too (an un-prefixed alias for the framework's `__loop.rejectionHistory` bag value; defaults to empty string on round 1).
- **Provider + model.** Which LLM. Most agents are `openai` / `gpt-5.4`.
- **Max tokens.** Upper bound on the model's response. **Set higher than you think you need** — tool-call args count against this budget.
- **Temperature.** 0.0–0.5 for deterministic agents (classifiers, committers); 0.3–0.5 for producers; 0.5+ for creative work.
- **Outputs.** The list of port names this agent can submit on. Wire matters — if the agent submits a port not in this list, the runtime rejects.
- **Partial pins.** Optional list of `@codeflow/<name>` partials to include in prompts via `{{ include "@codeflow/..." }}`. Pinning a partial freezes the version against the agent so a platform release that bumps a partial doesn't silently change the prompt. See [`features/prompt-partials.md`](features/prompt-partials.md).

### The `submit` tool

Every agent has a built-in `submit` tool that terminates its turn. The submit call carries a port name (must be in the declared outputs). **Critical**: the artifact handed downstream is the agent's **assistant message content**, not anything you put in the submit payload. Always tell the model:

> Write your full output as the assistant message content BEFORE calling `submit`.

If the model calls submit with empty content, the loop pushes back a reminder and burns a round. Don't tempt this — make it explicit. (V2 hard-rejects empty submissions on non-content-optional ports; if you're hitting it, declare the port as content-optional or fix the prompt.)

For sentinel ports (e.g. `Cancelled`, `Skip`) where the decision is the meaning and downstream consumers don't read the artifact body, set `contentOptional: true` on the output declaration:

```json
{ "kind": "Approved" },
{ "kind": "Cancelled", "contentOptional": true }
```

The implicit `Failed` port is always content-optional regardless of the flag.

### Mid-turn `setWorkflow` / `setContext`

The `setWorkflow` and `setContext` tools are also built-in. Use them to write small, structured values. Examples from the PRD pipeline: `setWorkflow("requestKind", "NewProject")`, `setWorkflow("requestSummary", "<one-paragraph>")`.

**Hard limits:**
- 16 KiB per call (V1). The runtime returns a typed tool error pointing you at the output-script pattern below if you exceed.
- Reserved variable namespaces are rejected (V1 + ProtectedVariableTargetRule). Today these are `workDir`, `traceId`, and `__loop.*` — the framework owns them.

**Don't use mid-turn `setWorkflow` to move large content** (PRDs, plans, codebases). The model has to emit the entire value as JSON tool-call args, eating into `max_tokens`. Once the args JSON is truncated mid-string, you get `JsonReaderException` and the trace fails. Use input/output scripts (next section) — or better, the [P4 mirror feature](features/mirror-output-to-workflow-var.md) which makes this a one-checkbox config on the agent node.

## Input scripts and output scripts

Most node kinds (Start, Agent, Hitl) carry two optional scripts:

- **Input script** — runs server-side BEFORE the agent invocation. Sees the upstream artifact as `input`. Can call `setWorkflow`, `setContext`, `setInput(text)` (replaces the artifact), `log()`. Doesn't need to call `setNodePath` — the agent runs after.
- **Output script** — runs server-side AFTER the agent completes. Sees the agent's output as `output` (with `output.decision` = the chosen port name and `output.text` = the message body). Can call `setWorkflow`, `setContext`, `setOutput(text)` (replaces what flows downstream), `setNodePath(portName)` (overrides the agent's choice), `log()`.

These scripts are JavaScript inside a sandbox. Use them when the framework features below don't fit; for the common cases (capture content into the bag, replace artifact on a port, accumulate rejection history) **prefer the declarative features**:

| If you would have written | Use instead |
|---|---|
| `setWorkflow('currentPlan', output.text)` in an outputScript | [P4 mirror](features/mirror-output-to-workflow-var.md): `mirrorOutputToWorkflowVar: "currentPlan"` on the node |
| `if (output.decision === 'Approved') setOutput(workflow.currentPlan)` | [P5 port replacement](features/replace-artifact-from-workflow-var.md): `outputPortReplacements: { "Approved": "currentPlan" }` |
| Round-by-round rejection accumulation | [P3 rejection history](features/rejection-history.md): `rejectionHistory.enabled: true` on the ReviewLoop |

Use scripts when you need genuine computation (branching on workflow state, reshaping artifacts, custom counters). Don't use them as a copy of the framework features — they're harder to read and skip the validators that watch the declarative side.

## Loops: the ReviewLoop primitive

CodeFlow has exactly one loop primitive: the **ReviewLoop** node. It wraps a single child workflow and runs it repeatedly until either:

- The child exits on a port that is NOT the configured `loopDecision` → the ReviewLoop exits on that port.
- `reviewMaxRounds` rounds have run → the ReviewLoop synthesizes an `Exhausted` port and exits.

The author sets:

- **`SubflowKey` + `SubflowVersion`** — which child workflow.
- **`ReviewMaxRounds`** — the iteration cap.
- **`LoopDecision`** — the port name that means "iterate again." Defaults to `Rejected`. Cannot be `Failed`.
- **`RejectionHistory`** *(optional)* — `{ enabled: true, maxBytes: 32768, format: "Markdown" }`. When enabled, the framework accumulates the loop-decision artifact into `__loop.rejectionHistory` per round and exposes it to in-loop agents as `{{ rejectionHistory }}`. See [`features/rejection-history.md`](features/rejection-history.md).

Inside the loop, child agents and scripts get extra Scriban variables:

- `{{ round }}` — 1-indexed iteration number.
- `{{ maxRounds }}` — the configured cap.
- `{{ isLastRound }}` — boolean, true when `round >= maxRounds`. The framework also auto-injects the `@codeflow/last-round-reminder` partial into in-loop agent prompts unless `optOutLastRoundReminder` is set on the node (P2).
- `{{ rejectionHistory }}` — accumulated rejection artifacts when the parent ReviewLoop has `rejectionHistory.enabled: true`.

These are **only set inside ReviewLoop children**. In other contexts they default to 0/0/false/empty.

### The author/reviewer loop shape (canonical)

The cleanest way to author this shape is via the **ReviewLoop pair template** (S4): pick "ReviewLoop pair" from the New Workflow picker, give it a name prefix, and you get a producer + reviewer + inner workflow + outer ReviewLoop pre-wired with `@codeflow/producer-base` + `@codeflow/reviewer-base` partials, P3 rejection history enabled, and the iteration cap set. See [`features/workflow-templates.md`](features/workflow-templates.md).

If you build it by hand:

```
Inner workflow (the loop body):
  Start: producer-agent  → Drafted → reviewer-agent → Approved | Rejected

Outer ReviewLoop:
  subflowKey = inner-workflow
  loopDecision = "Rejected"   ← iterate when reviewer rejects
  reviewMaxRounds = 5         ← give up after 5 rounds
  rejectionHistory.enabled = true   ← framework accumulates findings

The loop's output ports:
  Approved   → exits cleanly (the producer-reviewer agreed)
  Exhausted  → max rounds without approval (typically routes to a HITL)
  Failed     → implicit error sink
```

This shape works for: PRD producer/reviewer, implementation-plan architect/reviewer, code dev/reviewer.

### Reviewer and producer prompt scaffolding

Authors used to memorize a list of "rules of thumb" — counter approval bias, write last-round reminders, accumulate rejection history, etc. The platform now ships these as stock Scriban partials under the `@codeflow/` scope. Pin them and stop re-writing them.

| Partial | Use on | What it provides |
|---|---|---|
| `@codeflow/reviewer-base` | Reviewer agents inside bounded loops | Approval bias, no "default to Rejected", no iteration-target language. |
| `@codeflow/producer-base` | Producer agents inside loops | Non-negotiable feedback language, no metadata sections, write-content-before-submit. |
| `@codeflow/last-round-reminder` | Auto-injected into ReviewLoop children (opt out via `optOutLastRoundReminder` on the node) | `{{ if isLastRound }}` block telling the model the round budget is exhausting. |
| `@codeflow/no-metadata-sections` | Any artifact-producing agent | Forbids "## Changes Made", "## Diff", inline rationale. |
| `@codeflow/write-before-submit` | Any agent submitting on non-sentinel ports | Reminder that the message body IS the artifact. |

Pin via the agent's `partialPins`:

```json
"partialPins": [
  { "key": "@codeflow/reviewer-base", "version": 1 }
]
```

Then `{{ include "@codeflow/reviewer-base" }}` in the system prompt. Bumping a partial is a deliberate platform-release action; pinned agents continue to render against the version they pinned. See [`features/prompt-partials.md`](features/prompt-partials.md).

## Subflows and composition

A Subflow node embeds another workflow as a single graph node. It's the composition primitive that lets you build large workflows from small reusable pieces.

### How data crosses the boundary

| What | Inherited by child? | Returned to parent? |
|------|---------------------|---------------------|
| Input artifact | Yes (the parent's output flowing into the Subflow node becomes the child's start input) | Yes (the child's terminal artifact becomes the Subflow node's output) |
| `workflow` bag | Yes (snapshot taken at spawn) | Yes (child's final workflow vars merge back into parent's) |
| `context` bag | **No** — child's local context starts empty | **No** — child's context dies with the child saga |

Practical consequence: **if you want data to survive across the subflow boundary, put it in `workflow`.** The PRD's `requestSummary` lives in the workflow bag so every subflow can see it. The lifecycle workflow's `repositories` array gets seeded into the workflow bag at trace start so the dev workflow's setup agent can read it from inside its own subflow saga.

### Subflow node ports are computed, not authored

When you drop a Subflow node, its output ports are determined by the child workflow's **terminal ports** — the union of port names that have no outgoing edge in the child. You don't get to pick. If the child has terminal ports `{Approved, RejectionAccepted}`, your Subflow node exposes those two, plus the implicit `Failed`.

### When to break out a subflow

Pull a subgraph into its own workflow when:

- Multiple parent workflows need the same logic (e.g., the PRD pipeline has a single requirements loop reused by new-project / feature / bugfix flows).
- The subgraph is large enough that flattening it would clutter the parent.
- You want to bound the subgraph's iteration with a ReviewLoop.

Don't pull out a subflow just because a sequence is long — flat is fine if it's not reused. Composition costs come from having to hop between workflow editors.

## HITL forms

A HITL node pauses the trace and shows a form. The human's submission becomes the next artifact.

The form is configured on the HITL agent itself (the "agent" reference for a HITL node points to a HITL form definition, not an LLM agent). Set:

- **Display name + description** — what the operator sees on the pending-HITL list.
- **`outputTemplate`** — a Scriban template that builds the artifact handed downstream from the operator's form fields. Reference `{{ input }}` (the artifact that arrived) and any custom field the form collects (e.g., `{{ answer }}`, `{{ editedPlan }}`).
- **Outputs** — port names the form's buttons can submit on. The form UI shows one button per declared output port.

### Common HITL patterns

- **Simple approval gate** — 2 ports: `Approved` and `Cancelled`. `outputTemplate: "{{ input }}"` passes the artifact through unchanged.
- **Edit-then-approve** — the form has an editable text area pre-filled with `{{ workflow.currentPlan }}`. `outputTemplate: "{{ if editedPlan }}{{ editedPlan }}{{ else }}{{ workflow.currentPlan }}{{ end }}"`. One `Approved` button.
- **Multi-action collapse** — when "Edit & Approve" and "Approve as-is" both lead to the same downstream, model them as ONE port (`Approved`) with the form deciding artifact body via `outputTemplate`. Saves edges and clarifies intent. Use separate ports only when downstream behavior actually differs (e.g., `Approved` vs `RejectionAccepted`).

## Logic nodes

A Logic node is pure JavaScript — no LLM call, no token cost. Use it for routing decisions you can compute deterministically.

The script lives in the node's `outputScript` field (despite the kind being "Logic" — naming is historical). It runs with `input`, `context`, `workflow`, and the loop bindings if inside a ReviewLoop. It MUST call `setNodePath(portName)` to pick which output port to take.

### Common Logic patterns

- **Branch on global state** — at end of dev-flow, check `workflow.taskStatus.tasks`; if any are blocked/deferred, route to a HITL escalation; otherwise route straight to publish.
- **Increment a counter** — `setContext('attempts', (context.attempts || 0) + 1)` then `setNodePath('Continue')`.
- **Reshape an artifact** — combine fields from input/context/workflow vars into a structured artifact via `setOutput(...)`, then `setNodePath('Continue')`.

Logic nodes are ~10ms; agents are 1–60s and cost real money. Pull every routing decision you can into a Logic node.

## Tools and roles

Agents do real work (clone repos, edit files, run commands) by calling **host tools**. The currently registered host tools:

- `read_file(path)` — reads a file inside the workspace.
- `apply_patch(patch)` — applies a structured patch to files.
- `run_command(command, args?, workingDirectory?, timeoutSeconds?)` — runs an executable. The "shell" tool — git, npm, dotnet, pytest all go through this.
- `echo(text)` / `now()` — utilities, mostly for debugging.
- `vcs.open_pr(...)` — opens a pull request via the configured Git host. Authentication is platform-managed.

By default, agents have **no host tools** — only the platform built-ins (`submit`, `setWorkflow`, `setContext`). To grant tools, an agent needs a **role assignment**.

### Granting tools via roles

Roles are managed on the Roles page. A role bundles tool grants. To give an agent the ability to clone repos, you'd:

1. Pick the **`code-worker`** system role (S1: shipped pre-seeded with `read_file`, `apply_patch`, `run_command`, `echo`, `now`).
2. Open each agent that needs tool access (developer, reviewer, code-setup, etc.) and assign the `code-worker` role.

Other system-managed roles:
- **`read-only-shell`** — `read_file`, `run_command`, `echo`, `now`. For inspector / reporter agents that should never mutate the workspace.
- **`kanban-worker`** — pre-wired MCP grants for the conventional `kanban` MCP server.

System-managed roles can be assigned to your agents but cannot be edited or deleted via the API (the platform keeps their grants in sync with the host-tool catalog). To customize, fork to a new key.

Without a role assignment, an agent can think and write but can't touch the filesystem or run commands. **Pure-text agents** (PRD producer, classifier, post-mortem) don't need any role.

### MCP servers

If you've configured an MCP server (e.g., the Kanban MCP), grant access via a role that lists `mcp:<server-key>:<tool-name>` identifiers. Agents with that role get the MCP tools alongside the host ones.

## Templates: scaffolding workflows in seconds

The "New from Template" picker on the Workflows page collapses 30 minutes of wiring into 30 seconds. Available templates:

- **Empty workflow** — a single Start node + placeholder agent. Use when you want full structural control.
- **HITL approval gate** — a standalone trigger → Hitl form workflow with `Approved` + `Cancelled` ports. Drop it as a Subflow node anywhere you want a human checkpoint.
- **ReviewLoop pair** — producer + reviewer + inner workflow + outer ReviewLoop with `@codeflow/*` partials and P3 rejection-history pre-enabled. The canonical "draft, critique, finalize" shape.

Each template prompts for a name prefix and materializes 5+ entities (agents + workflows) at v1 with that prefix. Templates fail with a clear 409 if any of their planned keys collide with existing entities — pick a different prefix.

See [`features/workflow-templates.md`](features/workflow-templates.md) for the full template list and how to register new ones.

## Validation: what gets caught at save time

The platform runs every workflow save through a pluggable validation pipeline. Errors block the save; warnings surface in the editor's results panel without blocking. Cards V1-V8 + VZ2 + ProtectedVariableTargetRule cover the rules below; each rule has a stable `ruleId` you can grep telemetry for.

| Rule id | Severity | What it catches |
|---|---|---|
| `port-coupling` (V4) | Error | Node wires a port the agent cannot submit on (agent's declared outputs don't include it). The branch is unreachable. |
| `port-coupling` | Warning | Agent declares a port nothing wires — dead branch. |
| `missing-role` (V5) | Error | Agent prompt references a host-tool capability (`read_file`, `apply_patch`, `run_command`, `vcs.open_pr`, `mcp:*`) but the agent has zero role assignments. The tool call will fail at runtime. |
| `missing-role` | Warning | Agent has zero roles regardless of prompt content (allowed for pure-text agents). |
| `backedge` (V6) | Warning | An edge targets a node already reachable from its source — i.e., a cycle outside ReviewLoop's structural iteration. Set `intentionalBackedge: true` on the edge to dismiss. |
| `prompt-lint` (V7) | Warning | Reviewer prompt contains a forbidden phrase: `default to Rejected`, `you must always reject`, `the goal is N iterations`, `keep iterating until`. Switch to `@codeflow/reviewer-base`. |
| `package-self-containment` (V8) | Error | Export references an entity not in the package. Fix: bump every transitively-referenced entity into the package. |
| `protected-variable-target` (P4/P5 follow-on) | Error | A node's `mirrorOutputToWorkflowVar` or `outputPortReplacements` value targets a reserved namespace (`__loop.*`, `workDir`, `traceId`). |
| `workflow-vars-declaration` (VZ2) | Warning | Workflow declares `workflowVarsReads` / `workflowVarsWrites` and an agent reads / script writes a variable not in the declaration. Opt-in. |

V1 (mid-turn `setWorkflow` size cap) and V2 (empty-content rejection on non-content-optional ports) are runtime invariants, not save-time validators — they fail the in-flight tool call with a typed error rather than blocking save.

## Inspector and preview tools

The platform exposes a static-analysis service (F2) over `GET /api/workflows/{key}/{version}/dataflow`. The response carries a per-node snapshot:

- `workflowVariables`: every key reachable from upstream `setWorkflow` calls, mirror flags, and rejection-history sources, with `Definite` vs `Conditional` confidence.
- `contextKeys`: same shape, scoped to per-saga context.
- `inputSource`: which upstream node + port produces this node's input.
- `loopBindings`: round / maxRounds shown when the node is a ReviewLoop.

Today this drives the validator (VZ2). Frontend consumers — VZ1 data-flow inspector panel, VZ3 live prompt preview, E1 script `.d.ts` narrowing, E3 prompt-template autocomplete — are deferred until the editor work in Phase 6 lands.

## Testing: dry-run + replay

T1 dry-run mode (mock LLM responses, runtime walks the graph using fixtures) and T2 replay-with-edit (reproduce a past trace with one decision flipped) are deferred to a later epic. For now:

- Run a workflow with a small, cheap input and watch the trace UI.
- Use the existing trace inspector to see per-node decisions and artifacts.
- Compare workflow versions by exporting both packages and diffing the JSON.

## Versioning and the package format

Every workflow and agent is identified by `(key, version)`. Versions are immutable. To change something, you create a new version.

When you bump:

- A workflow node references a specific agent version (`agentKey` + `agentVersion`). If the agent bumps, the node still pins the old version unless you update it.
- A Subflow / ReviewLoop node references a specific child workflow version. Same pinning.

So changing one agent cascades: agent v2 → workflows that pin it bump to v2 (because their node graph changed) → workflows that embed those bump → eventually you stop at the entry point. The **cascade-bump assistant** (E4: `POST /api/workflows/cascade-bump/plan` and `/apply`) walks this dependency tree for you and creates the bumped versions in a single transactional sweep.

### Importing/exporting via packages

The Workflows page has Import / Export buttons. A **package** is a JSON file containing one workflow plus all its transitively-referenced workflows, agents, and roles, all at specific versions. Two rules to remember:

1. **Packages must be self-contained.** Every reference (subflow or agent) in the package must resolve to another entity in the same package. The importer does NOT look at your DB to fill in missing references. If you're bumping just one agent, you still need to include every other entity it pins, even unchanged ones at their existing version. (The `package-self-containment` validator V8 catches this at export.)
2. **Importing the same version twice is a no-op upsert** if the definition matches. If it differs, you get a Conflict and the import refuses. To resolve: bump versions of the conflicting entities, or delete the existing entries first.

For one-off wiring workflows (like a lifecycle that just chains existing subflows), the workflow CREATE endpoint (`POST /api/workflows`) is more forgiving — it resolves references against your DB instead of requiring self-containment. Use the UI's Save button or the endpoint directly when you don't want to re-package the world.

## Best practices

- **Pick a template.** S4's ReviewLoop pair is the right starting shape for any "draft, critique, finalize" workflow. Don't hand-roll the wiring.
- **Pin `@codeflow/*` partials.** Every reviewer should use `@codeflow/reviewer-base`. Every producer-in-a-loop should use `@codeflow/producer-base`. Authors who try to re-derive the rules-of-thumb miss things.
- **Use declarative features over scripts** for the common cases (P3 rejection-history, P4 mirror, P5 port replacement). Scripts are still the right answer for genuine computation.
- **Keep prompts focused.** One agent, one job. If a prompt is doing classification AND routing AND content generation AND state mutation, split it.
- **Use input/output scripts to move data**, not agent tool calls, when the data is large. (And prefer the P4/P5 features when they fit.)
- **Pass cross-subflow state via the workflow bag**, not context. Context dies at the subflow boundary.
- **Make Logic nodes do routing**, not agents. Cheaper, faster, deterministic.
- **Write substantive message content before submit.** The artifact downstream IS your message content. Empty content = empty artifact = downstream confusion. (V2 enforces this on non-content-optional ports.)
- **Set max_tokens generously**, especially when the agent is going to use mid-turn tools. Tool-call args eat into the budget.
- **Document your port names**. `Approved` / `Rejected` / `Continue` / `Cancelled` are clearer than `OK` / `KO` / `Yes` / `No`.

## Common pitfalls

The list shrunk because the platform now catches most of these before runtime. Each pitfall below names the validator or feature that resolves it.

- **`No tool output found for function call <id>`** — caught by V3 (function-call/function-call-output protocol assertion). If you ever see it, file a bug — it indicates a buggy retry path, not a workflow problem.
- **Reviewer rejects every iteration** — caught by V7 prompt-lint at save time when the prompt contains `default to Rejected`, `you must always reject`, `the goal is N iterations`, or `keep iterating until`. Fix: pin `@codeflow/reviewer-base`.
- **Producer ignores reviewer feedback** — pin `@codeflow/producer-base` for the non-negotiable-feedback principle and forbidding metadata sections.
- **Round budget always exhausts on bounded loops** — auto-injected `@codeflow/last-round-reminder` (P2) gives the model permission to ship on the final iteration. Toggle off via `optOutLastRoundReminder` if you have a stricter loop.
- **Workflow "completes" without reaching the right port** — caught by V4 port-coupling at save: agent's declared outputs and the node's wired ports must match.
- **Tool-call failures with "no tools available"** — caught by V5 missing-role at save when the prompt references a host tool capability and the agent has zero roles.
- **Backedge surprise** (a Logic or HITL routes BACK into a node that already ran) — caught by V6 backedge at save. Fix the wiring or set `intentionalBackedge: true` if the cycle is deliberate.
- **`JsonReaderException` mid-turn** — caused by `setWorkflow('largeKey', <large value>)` truncating mid-string. Caught at runtime by V1 (16 KiB cap returns a typed tool error). Fix: use the [P4 mirror feature](features/mirror-output-to-workflow-var.md).
- **`workflow.foo` is null in a prompt template** — VZ2 catches this at save time when you opt in to declarations: `workflowVarsReads` lists a variable no upstream node writes. (Without VZ2 declarations, the F2 dataflow analyzer at `GET /api/workflows/{key}/{version}/dataflow` surfaces the same fact.)
- **Mirror or port-replacement targets a reserved namespace** — caught by `protected-variable-target` at save: `__loop.*`, `workDir`, `traceId` are framework-managed and never declarable as targets.
- **Importing a package fails with "missing X v4"** — caught by V8 package-self-containment at export. The exporter now refuses to produce a package with dangling references; bump every transitively-referenced entity into the package.
- **Subflow child can't see context.foo** — design constraint, not a bug. Context dies at the subflow boundary; use the workflow bag.
- **HITL form shows blank fields** — the upstream agent submitted with empty message content. V2 catches this on non-content-optional ports; for sentinel ports, the form's `outputTemplate` should fall back: `{{ if input }}{{ input }}{{ else }}<placeholder>{{ end }}`.

## Example shapes worth copying

- **Bounded review loop** with HITL escalation: `producer → reviewer → loop ports → on Exhausted, HITL with edit-and-approve / abandon`. Used by impl-plan; covered by S4 template.
- **Outer task iteration** with inner per-task review loop: PM picks next task → inner ReviewLoop(developer↔reviewer) → on approve, commit + back to PM; on exhaust, mark blocked + back to PM. The dev-flow workflow is this shape.
- **Setup agent before a loop** to seed inputs into the workflow bag (e.g., clone repositories, init task lists). Lets the loop body reference inherited workflow vars instead of unpacking inputs every round.
- **Lifecycle wrapper** that chains specialized subflows with HITL gates between them: PRD intake → impl-plan → HITL gate → dev work → publish → post-mortem.

## When in doubt

- Prefer templates over hand-wired graphs.
- Prefer pinned `@codeflow/*` partials over custom prompt scaffolding.
- Prefer declarative features (P3/P4/P5) over output scripts.
- Prefer scripts over tool calls for moving data.
- Prefer workflow vars over context across subflow boundaries.
- Prefer Logic nodes over agents for deterministic routing.
- Prefer one PR at workflow end over many PRs per task.
- Prefer simple linear workflows over deeply nested loops, unless you actually need the iteration.
- Prefer explicit workflow-vars declarations on production workflows — VZ2 catches most "workflow.X is null" bugs at save.
