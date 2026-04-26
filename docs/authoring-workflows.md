# Authoring Workflows in CodeFlow

A practical guide for designing, building, and shipping CodeFlow workflows via the UI. Captures hard-won lessons from authoring the PRD intake, architect/reviewer plan, and dev-loop workflows.

> **About the `workflow` bag.** Each top-level trace owns a single `workflow` bag — a per-trace-tree key/value store that propagates *down* into subflows and ReviewLoops via copy-on-fork: children get a snapshot at the moment they spawn, and at child completion the child's final bag merges back into the parent. The bag is **not** process-wide and **not** shared across separate traces. Read in templates as `{{ workflow.X }}`, in scripts as `workflow.X`, written via `setWorkflow(...)`.

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
- **Prompt template.** Renders into the user message at invocation time, using Scriban (`{{ ... }}`). Variables include `{{ input }}` (the upstream artifact body), `{{ context.foo }}`, `{{ workflow.foo }}`, plus loop bindings inside ReviewLoops: `{{ round }}`, `{{ maxRounds }}`, `{{ isLastRound }}`.
- **Provider + model.** Which LLM. Most agents are `openai` / `gpt-5.4`.
- **Max tokens.** Upper bound on the model's response. **Set higher than you think you need** — tool-call args count against this budget.
- **Temperature.** 0.0–0.5 for deterministic agents (classifiers, committers); 0.3–0.5 for producers; 0.5+ for creative work.
- **Outputs.** The list of port names this agent can submit on. Wire matters — if the agent submits a port not in this list, the runtime rejects.

### The `submit` tool

Every agent has a built-in `submit` tool that terminates its turn. The submit call carries a port name (must be in the declared outputs). **Critical**: the artifact handed downstream is the agent's **assistant message content**, not anything you put in the submit payload. Always tell the model:

> Write your full output as the assistant message content BEFORE calling `submit`.

If the model calls submit with empty content, the loop pushes back a reminder and burns a round. Don't tempt this — make it explicit.

### Mid-turn `setWorkflow` / `setContext`

The `setWorkflow` and `setContext` tools are also built-in. Use them to write small, structured values. Examples from the PRD pipeline: `setWorkflow("requestKind", "NewProject")`, `setWorkflow("requestSummary", "<one-paragraph>")`.

**Warning**: do NOT use mid-turn `setWorkflow` to move large content (PRDs, plans, codebases). The model has to emit the entire value as JSON tool-call args, eating into `max_tokens`. Once the args JSON is truncated mid-string, you get `JsonReaderException` and the trace fails. **Use input/output scripts instead** (see below).

## Input scripts and output scripts

Most node kinds (Start, Agent, Hitl) carry two optional scripts:

- **Input script** — runs server-side BEFORE the agent invocation. Sees the upstream artifact as `input`. Can call `setWorkflow`, `setContext`, `setInput(text)` (replaces the artifact), `log()`. Doesn't need to call `setNodePath` — the agent runs after.
- **Output script** — runs server-side AFTER the agent completes. Sees the agent's output as `output` (with `output.decision` = the chosen port name and `output.text` = the message body). Can call `setWorkflow`, `setContext`, `setOutput(text)` (replaces what flows downstream), `setNodePath(portName)` (overrides the agent's choice), `log()`.

These scripts are JavaScript inside a sandbox. **Use them aggressively** — they're faster, cheaper, and more deterministic than asking the LLM to do the same work via tool calls.

### Two patterns that come up constantly

**Pattern 1 — Capture large content into the workflow bag.** The architect agent writes a 5000-token implementation plan as its message body. Don't ask it to call `setWorkflow('currentPlan', <plan>)` — token cost doubles. Instead:

```javascript
// Output script on the architect node
setWorkflow('currentPlan', output.text);
```

The LLM emits the plan once (as message body); the script mirrors it into the workflow bag server-side.

**Pattern 2 — Replace the artifact with a workflow.** The reviewer approves; you want the loop's exit artifact to be the full plan, but you don't want the reviewer to re-emit thousands of tokens just to echo it. Have the reviewer write a brief approval rationale, then:

```javascript
// Output script on the reviewer node
if (output.decision === 'Approved') {
  setOutput(workflow.currentPlan);
} else if (output.decision === 'Rejected') {
  // Accumulate findings into a rejection history
  var prior = workflow.rejectionHistory || '';
  setWorkflow('rejectionHistory', prior ? prior + '\n\n## Round ' + round + '\n' + output.text : '## Round ' + round + '\n' + output.text);
}
```

`setOutput` swaps the artifact downstream; the reviewer's "Plan approved." note is logged in the trace but doesn't reach the next node.

## Loops: the ReviewLoop primitive

CodeFlow has exactly one loop primitive: the **ReviewLoop** node. It wraps a single child workflow and runs it repeatedly until either:

- The child exits on a port that is NOT the configured `loopDecision` → the ReviewLoop exits on that port.
- `reviewMaxRounds` rounds have run → the ReviewLoop synthesizes an `Exhausted` port and exits.

The author sets:

- **`SubflowKey` + `SubflowVersion`** — which child workflow.
- **`ReviewMaxRounds`** — the iteration cap.
- **`LoopDecision`** — the port name that means "iterate again." Defaults to `Rejected`. Cannot be `Failed`.

Inside the loop, child agents and scripts get three extra Scriban variables:

- `{{ round }}` — 1-indexed iteration number.
- `{{ maxRounds }}` — the configured cap.
- `{{ isLastRound }}` — boolean, true when `round >= maxRounds`.

These are **only set inside ReviewLoop children**. In other contexts they default to 0/0/false.

### The author/reviewer loop shape (canonical)

```
Inner workflow (the loop body):
  Start: producer-agent  → Drafted → reviewer-agent → Approved | Rejected

Outer ReviewLoop:
  subflowKey = inner-workflow
  loopDecision = "Rejected"   ← iterate when reviewer rejects
  reviewMaxRounds = 5         ← give up after 5 rounds

The loop's output ports:
  Approved   → exits cleanly (the producer-reviewer agreed)
  Exhausted  → max rounds without approval (typically routes to a HITL)
  Failed     → implicit error sink
```

This shape works for: PRD producer/reviewer, implementation-plan architect/reviewer, code dev/reviewer.

### Reviewer prompt rules of thumb

Reviewers want to reject. Counterbalance:

1. **Explicit approval bias**: "Approve when there are no MAJOR gaps. Nitpicks are NOT blocking."
2. **Last-round reminder**: `{{ if isLastRound }}**LAST ROUND** — approve unless there is a critical gap.{{ end }}` — gives the model permission to ship.
3. **Rejection history**: feed prior rejections back so the reviewer doesn't re-litigate concerns the producer already addressed. Accumulate via output script (Pattern 2 above).
4. **Never write "default to rejected" or "the goal is N iterations"** — the model takes those literally and you get N rejections every time.

### Producer prompt rules of thumb

Producers want to argue with feedback. Counterbalance:

1. Make addressing every finding **non-negotiable**: "Read every finding. For each, modify the relevant section to fully satisfy it. Do NOT defer, do NOT partially address, do NOT explain why the finding doesn't apply."
2. Close the "I disagree" loophole: "If you genuinely believe a finding is wrong, still adjust the wording so the same misreading can't happen again."
3. **Forbid metadata sections** in the artifact body: no "## Changes Made", no "## Diff", no inline notes. The artifact must be a clean standalone document — the reviewer compares against the prior critique on its own.

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

By default, agents have **no host tools** — only the platform built-ins (`submit`, `setWorkflow`, `setContext`). To grant tools, an agent needs a **role assignment**.

### Granting tools via roles

Roles are managed on the Roles page. A role bundles tool grants. To give an agent the ability to clone repos, you'd:

1. Create a `code-worker` role (or use the existing one shipped in `dev-flow-v1-package.json`).
2. Grant it host tools: `read_file`, `apply_patch`, `run_command` (and optionally `echo`, `now`).
3. Open each agent that needs tool access (developer, reviewer, code-setup, etc.) and assign the `code-worker` role.

Without a role assignment, an agent can think and write but can't touch the filesystem or run commands. **Pure-text agents** (PRD producer, classifier, post-mortem) don't need any role.

### MCP servers

If you've configured an MCP server (e.g., the Kanban MCP), grant access via a role that lists `mcp:<server-key>:<tool-name>` identifiers. Agents with that role get the MCP tools alongside the host ones.

## Versioning and the package format

Every workflow and agent is identified by `(key, version)`. Versions are immutable. To change something, you create a new version.

When you bump:

- A workflow node references a specific agent version (`agentKey` + `agentVersion`). If the agent bumps, the node still pins the old version unless you update it.
- A Subflow / ReviewLoop node references a specific child workflow version. Same pinning.

So changing one agent cascades: agent v2 → workflows that pin it bump to v2 (because their node graph changed) → workflows that embed those bump → eventually you stop at the entry point.

### Importing/exporting via packages

The Workflows page has Import / Export buttons. A **package** is a JSON file containing one workflow plus all its transitively-referenced workflows, agents, and roles, all at specific versions. Two rules to remember:

1. **Packages must be self-contained.** Every reference (subflow or agent) in the package must resolve to another entity in the same package. The importer does NOT look at your DB to fill in missing references. If you're bumping just one agent, you still need to include every other entity it pins, even unchanged ones at their existing version.
2. **Importing the same version twice is a no-op upsert** if the definition matches. If it differs, you get a Conflict and the import refuses. To resolve: bump versions of the conflicting entities, or delete the existing entries first.

For one-off wiring workflows (like a lifecycle that just chains existing subflows), the workflow CREATE endpoint (`POST /api/workflows`) is more forgiving — it resolves references against your DB instead of requiring self-containment. Use the UI's Save button or the endpoint directly when you don't want to re-package the world.

## Best practices

- **Keep prompts focused.** One agent, one job. If a prompt is doing classification AND routing AND content generation AND state mutation, split it.
- **Bias reviewers toward approval.** They lean reject by default. Counter explicitly.
- **Use `isLastRound` reminders** in any reviewer inside a bounded loop. It's the difference between "always exhausted at max rounds" and "approves when good enough."
- **Use input/output scripts to move data**, not agent tool calls, when the data is large.
- **Pass cross-subflow state via the workflow bag**, not context. Context dies at the subflow boundary.
- **Make Logic nodes do routing**, not agents. Cheaper, faster, deterministic.
- **Write substantive message content before submit.** The artifact downstream IS your message content. Empty content = empty artifact = downstream confusion.
- **Set max_tokens generously**, especially when the agent is going to use mid-turn tools. Tool-call args eat into the budget.
- **Document your port names**. `Approved` / `Rejected` / `Continue` / `Cancelled` are clearer than `OK` / `KO` / `Yes` / `No`.

## Common pitfalls

- **JsonReaderException at byte ~1500**: an agent tried to `setWorkflow('largeKey', <large value>)` mid-turn and ran out of tokens mid-string. Move the work to an output script that uses `output.text`.
- **`workflow.foo` is null in a prompt template**: the global wasn't seeded by the time this agent ran. Common cause: top-level Start agent's input script ran but didn't propagate (engine versions before 2026-04-26 had this bug). Verify the engine fix is deployed.
- **`No tool output found for function call <id>`**: an OpenAI Responses-API protocol error. Almost always means a buggy retry path in the loop — every prior assistant `function_call` must have a matching `function_call_output` in the next request. Filed and fixed for the empty-content submit retry; if you see it elsewhere, suspect a similar gap.
- **Reviewer rejects every iteration**: prompt explicitly biases toward rejection. Remove "default to Rejected" / "the goal is N iterations" language. Add approval bias + isLastRound reminder.
- **Producer ignores reviewer feedback**: prompt is too soft on the requirement to address every finding. Add the non-negotiable language. Add rejection-history accumulation so the reviewer can call out un-addressed prior findings on the next round.
- **Workflow "completes" without reaching the right port**: the agent didn't call `submit`, or called it on a port not in declared outputs. Check the agent's outputs list against the workflow node's outputPorts.
- **Tool-call failures with "no tools available"**: agent has no role assignments. Create or assign a role with the host tools.
- **Subflow child can't see context.foo**: it's not inherited. Move it to `workflow.foo` at the parent.
- **Importing a package fails with "missing agent X v4"**: the package isn't self-contained. Add all referenced entities.
- **HITL form shows blank fields**: the upstream agent submitted with empty message content, so `{{ input }}` rendered to nothing. Check the agent's CRITICAL OUTPUT RULE.
- **Branch is created twice on the same trace**: usually a backedge bug — a Logic or HITL routes BACK into a node that already ran, kicking off another setup. Verify the loop isn't re-entering its own start.

## Example shapes worth copying

- **Bounded review loop** with HITL escalation: `producer → reviewer → loop ports → on Exhausted, HITL with edit-and-approve / abandon`. Used by impl-plan, reusable for any "draft, critique, finalize" pattern.
- **Outer task iteration** with inner per-task review loop: PM picks next task → inner ReviewLoop(developer↔reviewer) → on approve, commit + back to PM; on exhaust, mark blocked + back to PM. The dev-flow workflow is this shape.
- **Setup agent before a loop** to seed inputs into the workflow bag (e.g., clone repositories, init task lists). Lets the loop body reference inherited workflow vars instead of unpacking inputs every round.
- **Lifecycle wrapper** that chains specialized subflows with HITL gates between them: PRD intake → impl-plan → HITL gate → dev work → publish → post-mortem.

## When in doubt

- Prefer scripts over tool calls for moving data.
- Prefer workflow vars over context across subflow boundaries.
- Prefer Logic nodes over agents for deterministic routing.
- Prefer one PR at workflow end over many PRs per task.
- Prefer simple linear workflows over deeply nested loops, unless you actually need the iteration.
- Prefer explicit approval bias and last-round reminders in any reviewer; the model defaults to rejection without them.
