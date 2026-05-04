---
key: workflow-authoring
name: Workflow authoring
description: Draft, refine, and save importable workflow packages.
trigger: any user request to create / draft / save / import / package a workflow.
---

# Workflow authoring

This skill carries the curriculum the assistant needs to drive a focused,
multi-turn dialogue with the user and emit a complete, importable workflow
package. The package is a draft only — the user explicitly clicks Save (or
imports the JSON file) to persist anything to the library.

## READ THIS FIRST: jump to the canonical exemplar

Before you write a single line of package JSON, scroll to the end of this
skill ("Canonical shape exemplar") and read it. It is a complete,
importable package showing every field name, enum casing, and nesting
the importer expects. Mirror it exactly. Common shape mistakes that
guarantee a save round-trip:

- The schema field is `schemaVersion`, not `$schema` or `schema`. Its value
  is the literal string `"codeflow.workflow-package.v1"`.
- Node `id` MUST be a fresh GUID (e.g., `"11111111-1111-1111-1111-000000000001"`).
  Slug-style ids like `"start-node"` are rejected by the typed binding.
- Workflow `key` is slug-shaped (`lowercase-dashed`), but node `id` is a GUID.
  These are different rules — don't mix them up.

If a save attempt rejects on a structural / shape issue, your next
action is to re-read the exemplar at the bottom of this skill — NOT
another save attempt with a guess.

## Authoring vocabulary

### Agents
Reusable AI-agent configurations. Each agent has a `key`, a `version`, a
Scriban prompt template, a system prompt, a provider+model+temperature+max_tokens,
a list of declared output ports, optional `partialPins`, and zero or more
role assignments that control which tools/skills/MCP servers it can call.
Agents may also carry up to five library `tags` for browsing; workflow
packages preserve those tags on embedded `agents[]`.
Agents are referenced from workflow nodes by `agentKey + agentVersion`.
Editing an agent in-place from the workflow canvas forks-on-save and
supports publish-back with drift detection.

#### Agent built-in tools (always available, no role required)
Every agent has three platform-managed tools wired into its turn:
- `submit({ port, ... })` — terminates the turn on the chosen output port.
  The artifact handed downstream is the agent's **assistant message
  content**, not the submit payload. The model must write its full output
  as the message body BEFORE calling `submit`. Empty content is
  hard-rejected on non-content-optional ports (V2). Mark sentinel-only
  ports (`Cancelled`, `Skip`) `contentOptional: true` if the body really
  shouldn't carry an artifact. The implicit `Failed` port is always
  content-optional.
- `setWorkflow(key, value)` — writes a small structured value into the
  trace's `workflow` bag. Limits: 16 KiB per call, 256 KiB cumulative per
  turn. Reserved keys (`traceWorkDir`, `traceId`, `__loop.*`) are rejected.
  **Use `setWorkflow` for `repositories`** — the per-trace VCS allowlist
  lives on the `workflow` bag (saga-field-backed), not `context`.
- `setContext(key, value)` — same shape, but writes into the per-saga
  `context` bag (does NOT cross the subflow boundary).

For large content (PRDs, plans, codebases) DON'T use mid-turn `setWorkflow`
— the args JSON eats `max_tokens` and truncates. Use input/output scripts,
or the declarative P4 mirror feature (see "Declarative authoring features"
below).

#### Invocation budget (optional `budget` block)
Every agent invocation runs under three guardrails. The defaults
(`16` tool calls / `5` minutes wall-clock / `8` consecutive non-mutating
calls) cover almost every author/reviewer/synthesizer agent. Override only
when an agent legitimately needs a higher ceiling — for example a
synthesizer that walks many files via `read_file` before writing.

Shape (all three fields are optional; omit the whole block to use defaults):

```json
"budget": {
  "maxToolCalls": 32,
  "maxLoopDuration": "00:10:00",
  "maxConsecutiveNonMutatingCalls": 16
}
```

- `maxToolCalls` — total tool invocations allowed in one turn.
  Validator bounds: integer in `[1, 256]`. Exceeding fails the loop with
  `ToolCallBudgetExceeded`.
- `maxLoopDuration` — hard wall-clock cap. TimeSpan string in
  `"HH:mm:ss"` (e.g. `"00:10:00"`). Validator bounds: 1 second to 1 hour.
- `maxConsecutiveNonMutatingCalls` — how many read-only tool calls in a row
  before the loop bails. Catches "I'll just read one more file" loops where
  the agent never calls `submit`. Validator bounds: integer in `[1, 256]`,
  and must not exceed `maxToolCalls` if both are set.

The agent editor exposes these on the **Model** tab under "Invocation
budget"; in a workflow package they live inside `agents[].config.budget`.
Don't bump these to mask a buggy prompt — fix the prompt instead.

#### Sub-agents (`spawn_subagent`) — agent config, NOT a node kind
An agent can delegate sub-tasks to anonymous worker LLMs it parameterises
at runtime. This is **per-agent configuration**, not a workflow node, and
the parent agent decides at its own turn-time how many workers to spawn
and what each one does. When a developer-style agent needs to fan helper
work out (read these N files, score these M proposals, draft these K
variants), THIS is the right primitive — not a Swarm node.

Enable it by adding a `subAgents` block to the agent's config:

```jsonc
"subAgents": {
  "provider": "anthropic",         // null/omit → inherit parent's provider
  "model": "claude-haiku-4-5",     // null/omit → inherit
  "maxConcurrent": 4,              // 1..32, default 4
  "maxTokens": 8000,               // null/omit → inherit
  "temperature": 0.2               // 0..2, null/omit → inherit
}
```

Validator bounds: `maxConcurrent ∈ [1, 32]`, `maxTokens ∈ [1, 200_000]`,
`temperature ∈ [0, 2]`.

When `subAgents` is set, the runtime adds a built-in `spawn_subagent` tool
to the parent's turn. The parent calls it with one or more invocations,
each carrying a per-call `systemPrompt` + `input`:

```jsonc
spawn_subagent({
  "invocations": [
    { "systemPrompt": "You are a focused worker. Reply as JSON {findings: string[]}.", "input": "Read the README and list anything that contradicts the docs." },
    { "systemPrompt": "You are a critic. Score 1–5 and explain.", "input": "Score the proposed approach in <input/>" }
  ]
})
```

Returns `[{ input, output, decision }, ...]` in request order. Workers
inherit the parent's resolved tool set (host + MCP + role grants) — there
is no per-spawn role override in v1. Workers cannot recursively spawn
(child config has `subAgents: null` always); depth is capped at 1 by
construction.

Use sub-agents when the orchestration is *internal to a single agent's
reasoning*. Use a Swarm node when the protocol (Sequential / Coordinator)
is fixed at workflow-design time. Use a ReviewLoop when the iteration is
between drafter and reviewer. See "When to fan out" below.

### Prompt templates and partials
Agent prompts use Scriban 7.1 in a sandboxed renderer. Familiar syntax —
`{{ name }}`, `{{ for item in items }}…{{ end }}`, `{{ if cond }}…{{ end }}`,
partials. The renderer blocks file I/O and disables unsafe builtins.

Standard variables in templates:
- `{{ input }}` — the upstream artifact body.
- `{{ context.X }}` — per-saga context bag (local to one workflow's saga).
- `{{ workflow.X }}` — per-trace-tree workflow bag (propagates across
  subflows).
- Inside ReviewLoop children: `{{ round }}`, `{{ maxRounds }}`,
  `{{ isLastRound }}`, and `{{ rejectionHistory }}` when the parent loop
  has rejection history enabled.

**Pinned partials.** The platform ships stock `@codeflow/*` partials. Pin
them via the agent's `partialPins: [{ key, version }]` and include with
`{{ include "@codeflow/<name>" }}`. Pinning freezes the version against
the agent so a platform release that bumps a partial doesn't silently
change the prompt. Available partials:
- `@codeflow/reviewer-base` — approval-bias scaffolding for reviewer agents
  in bounded loops; forbids "default to Rejected" / iteration-target
  language.
- `@codeflow/producer-base` — non-negotiable-feedback language for producer
  agents in loops; forbids metadata sections; reminds the model to write
  content before submit.
- `@codeflow/last-round-reminder` — auto-injected into ReviewLoop children
  unless the node sets `optOutLastRoundReminder`.
- `@codeflow/no-metadata-sections` — forbids "## Changes Made", "## Diff",
  inline rationale on artifact-producing agents.
- `@codeflow/write-before-submit` — reminds an agent that the message body
  IS the artifact.

### Roles, skills, MCP servers
- **Role**: a named bundle of grants attached to an agent. Controls which
  tools the agent can invoke during execution. An agent without any role
  still has the built-in `submit` / `setWorkflow` / `setContext` tools —
  but no host tools (filesystem, shell) and no MCP tools. Roles may carry
  up to five library `tags`; workflow packages preserve those tags on
  embedded `roles[]`.
- **Skill**: a reusable host-side capability surfaced through a role.
- **MCP server**: an external Model Context Protocol server registered as
  a tool source; agents pick up its tools via roles that list
  `mcp:<server-key>:<tool-name>` grants.
- Seeded system roles: `code-worker` (`read_file`, `apply_patch`,
  `run_command`, `echo`, `now`); `code-builder` (code-worker +
  `container.run` + `web_fetch` + `web_search` for agents that need to
  build/test in language toolchains the host doesn't ship); `read-only-shell`
  (no `apply_patch`); `kanban-worker` (pre-wired MCP grants).

### Workflows and nodes
A workflow is a directed graph of nodes joined by edges. Node kinds:
- **Start** — exactly one per workflow. References an agent. Trace entry.
- **Agent (A)** — invokes one agent; the agent's decision drives the next
  edge.
- **Hitl (H)** — pauses the trace for a human decision. References a HITL
  form (a kind of "agent" definition with an `outputTemplate` Scriban
  template + form-button output ports).
- **Subflow (S)** — invokes another workflow as a single node. Output ports
  are computed: the union of the child's terminal ports plus implicit
  `Failed`.
- **ReviewLoop (R)** — the only loop primitive. Wraps a single child
  workflow and iterates it until the child exits on a port that is NOT
  the configured `loopDecision` (then ReviewLoop exits on that port) or
  `reviewMaxRounds` is reached (then the loop synthesizes an `Exhausted`
  port). Output ports = child's terminal ports + `Exhausted` + `Failed`.
- **Swarm** — fans out to N contributor agents under a chosen protocol
  (`Sequential` or `Coordinator`), then a synthesizer agent emits the
  node's terminal output. Non-replayable.
- **Transform (T)** — pure data transformation, runs a Scriban template
  with no LLM call. Single synthesized output port `Out`.
- **Logic (L)** — routing-only node; runs a JS script to choose the next
  port without an LLM call. The script MUST call `setNodePath(portName)`.

### Ports and edges
Every node declares user-defined output ports plus an implicit `Failed`
port. Edges connect a `(sourceNode, sourcePort)` to a `(targetNode,
targetPort)`. The validation pipeline rejects unconnected ports and
port-coupling violations during workflow save.

### The `workflow` bag vs the `context` bag
Two key/value stores propagate state across a trace:
- **`workflow` bag** — per-trace-tree, copy-on-fork. Children get a
  snapshot at spawn; at child completion the child's final bag merges
  back. Read in templates as `{{ workflow.X }}`, in scripts as `workflow.X`,
  written via `setWorkflow(...)`. **If you want data to survive across
  the subflow boundary, put it in `workflow`.** The framework-managed
  per-trace state lives here too: `workflow.traceWorkDir` (workspace
  path), `workflow.traceId` (32-char hex), and `workflow.repositories`
  (per-trace VCS allowlist consulted by `vcs_*` host tools).
- **`context` bag** — per-saga, local to one workflow. Read as
  `{{ context.X }}` / `context.X`, written via `setContext(...)`. Does
  NOT cross the subflow boundary. Don't put per-trace state here —
  notably, `setContext('repositories', ...)` does NOT widen the VCS
  allowlist; use `setWorkflow` for that.

### Routing scripts
Agent / HITL / Edge / Subflow nodes each have up to **two script slots**:
an input script and an output script, both Jint-evaluated JavaScript with
a 1 MiB output cap.

Script primitives:
- `setWorkflow(key, value)` / `setContext(key, value)` — write to the bags.
- `setOutput(text)` — replace the artifact flowing downstream from this
  node. Output scripts only.
- `setInput(text)` — replace the artifact flowing into this node. Input
  scripts only.
- `setNodePath(portName)` — override the chosen port. Output scripts on
  agent-attached nodes; required on Logic nodes.
- `log(message)` — append to the per-evaluation log buffer.

### Declarative authoring features (prefer over scripts)
For common patterns, the platform ships first-class node-level config
that the validator + runtime treat better than equivalent scripts:
- **P3 rejection history** — `rejectionHistory.enabled: true` (optional
  `maxBytes`, `format: "Markdown"`) on a ReviewLoop node. The framework
  accumulates the loop-decision artifact per round into
  `__loop.rejectionHistory` and exposes it as `{{ rejectionHistory }}`.
- **P4 mirror to workflow var** — `mirrorOutputToWorkflowVar: "currentPlan"`
  on a node. After successful completion the framework writes the output's
  body to `workflow.currentPlan`.
- **P5 port-keyed artifact replacement** —
  `outputPortReplacements: { "Approved": "currentPlan" }`. When the node
  exits on the named port, the framework substitutes the artifact with the
  value of the named workflow var.

Use scripts when you need genuine computation (branching on workflow state,
reshaping artifacts, custom counters). Don't reimplement the declarative
features in scripts — they skip the validators that watch the declarative
side.

### Workflow templates
The "New from Template" picker on the Workflows page collapses 30 minutes
of wiring into 30 seconds. Available templates:
- **Empty workflow** — single Start node + placeholder agent.
- **HITL approval gate** — trigger → Hitl form (`Approved` + `Cancelled`).
- **ReviewLoop pair** — producer + reviewer + inner workflow + outer
  ReviewLoop with `@codeflow/*` partials and P3 rejection-history
  pre-enabled. Recommend this for any author/reviewer flow.
- **Setup → loop → finalize** — setup agent (input-script seeds the
  workflow bag) → ReviewLoop → on Exhausted, HITL escalation.
- **Lifecycle wrapper** — three placeholder phase workflows chained by
  two HITL approval gates.

### Authoring code-aware workflows (clone → edit → commit → PR)

Code-aware workflows are the dev-flow pattern: clone repos, run agent
work over them, commit, push, open a PR. The shape is straightforward,
but a small set of design choices look correct in the package and fail
at runtime. These lessons came from real workflows that completed all
their LLM work and then failed at the publish step — the worst failure
mode because it strands all the development work on a local branch and
burns the entire token budget before surfacing the problem.

Reference: `docs/code-aware-workflows.md` is the canonical platform
contract.

**Auth is automatic, but only inside `git`.** The platform's per-trace
credential helper makes every spawned `git` process see
`credential.helper = store --file=…/{traceId:N}`. Authors don't need to
plumb tokens, write a credential helper of their own, or pass anything
through agent prompts. Two preconditions for it to work:

1. The trace's `repositories[]` input must include the host the workflow
   will push to. The cred file is built from
   `repositories[] ∩ configured GitHostSettings host`. If `repositories[]`
   doesn't include the configured host, the helper has no entry and
   `git push` fails with "Authentication failed".
2. The configured `GitHostSettings` must have `HasToken = true`. Out of
   authoring scope, but worth flagging to the user when designing a
   workflow that targets a host they may not have configured.

**Push on first commit, NOT at PR-publish time.** Whichever agent owns
committing (typically `task-committer` or any agent doing `git commit`)
must `git push -u origin <featureBranch>` IMMEDIATELY after the commit
lands. Do not wait for the publish agent at the end of the workflow.

- Pushing on the first commit exercises the credential boundary at task 1,
  when the workflow can still recover (clarify with HITL, fail loudly)
  instead of stranding all the development work on a local branch.
- Subsequent pushes are fast-forwards, basically free.
- The publish agent's `git push -u origin` becomes idempotent insurance,
  not the only chance.

A common anti-pattern is a workflow where only the final publish agent
ever pushes. That design ran the entire dev/review cycle (often hours
of LLM time) before discovering credentials were unavailable. Don't
emit it.

**Verify the base branch via `git ls-remote`, never silently default to
`main`.** The publish agent (or whichever agent calls `vcs.open_pr`)
must resolve the base branch in this order, stopping at the first that
succeeds:

1. If the trace's `context.repositories[i].branch` is set and non-empty,
   use it (it was the verified upstream the developer cloned from).
2. Otherwise run
   `run_command "git", ["ls-remote", "--symref", "origin", "HEAD"]`
   from the repo's `localPath`. The response's
   `ref: refs/heads/<X>\tHEAD` line gives the authoritative default
   branch — git's credential helper handles auth, so this works for
   private repos too.
3. Only as a last-resort fallback, call `vcs.get_repo` and use
   `defaultBranch`.

Do NOT default to `"main"`. Many repos use `master`, `develop`, `trunk`,
or a release branch as the merge target. A PR opened against the wrong
base is confusing manual cleanup that wastes the user's time.

**Use `vcs.clone` once, then plain `git`.** The narrow `vcs.*` surface
is intentional. Use:

- `vcs.clone` — establishes the workspace anchor and registers the
  clone path. Call once per repo from the setup agent.
- `vcs.open_pr` — the delivery boundary, policed by envelope axes and
  per-trace `repositories[]`.
- `vcs.get_repo` — host-API metadata (visibility, default branch, clone
  URL). Last-resort for base-branch resolution per above.

Everything else uses `run_command "git", [...]`. Do NOT add agent
prompts that re-implement git porcelain via REST or that try to mint
their own auth — the credential helper handles every git invocation
transparently.

**Scope `vcs.*` grants per agent.** Don't grant the whole `vcs.*` set
to every agent in the workflow. Typical scoping:

- Setup / first agent that materializes the workspace: `vcs.clone`
  (and `vcs.get_repo` if it needs to discover the upstream default
  branch before cloning).
- Developer / committer / general-work agents: NO `vcs.*` grants —
  they use `run_command "git", [...]` for everything.
- Publish agent: `vcs.open_pr` only.

Keeping the surface scoped makes the workflow's intent visible at the
role level and reduces the blast radius of a mis-prompted agent.

**`workflow.repositories`, NOT `context.repositories`.** Worth restating
in the code-aware context: the per-trace VCS allowlist lives on the
`workflow` bag (saga-field-backed), not `context`.
`setContext('repositories', ...)` does NOT widen the allowlist or
trigger cred-file rewrite. The platform routes the trace-launch input
into `workflow.repositories` automatically; if you ever need to add a
repo mid-flow (rare), use `setWorkflow`.

### When to fan out: sub-agents vs Swarm vs ReviewLoop vs Subflow
Pick the smallest primitive that fits — these are NOT interchangeable:

| Primitive | Picks at | Fan-out shape | Use when |
| --- | --- | --- | --- |
| **Sub-agents** (parent agent config + `spawn_subagent` tool) | Runtime, per parent turn | One reasoning agent → N anonymous workers it parameterises on the fly | A developer-style agent wants helper LLM calls (read N files, score M proposals, draft K variants) under its own reasoning. NOT a workflow node. |
| **Swarm node** | Workflow-design time | Author-fixed protocol: `Sequential` or `Coordinator`; N contributor agents + 1 synthesizer | The protocol is part of the workflow's design contract and the author wants the orchestration auditable from the canvas. Non-replayable. |
| **ReviewLoop node** | Workflow-design time | One drafter + one reviewer agent in a bounded iteration | Author/reviewer iteration with rejection feedback. The only loop primitive. |
| **Subflow node** | Workflow-design time | Inline composition of another workflow | Reuse an existing workflow's full graph as a single step inside a parent. |

Heuristic: **the user said "developer loop" / "delegate" / "have it spawn helpers"** → that's *sub-agents on the developer's agent config* feeding into a ReviewLoop, not a Swarm. Swarm = "I want N peers with a defined protocol picking at design time"; sub-agents = "the developer agent picks workers as it reasons".

### Swarm node
Source: `docs/swarm-node.md` is the canonical contract.

The Swarm node fans LLM work out to N contributor agents under a chosen
protocol and aggregates their drafts through a synthesizer agent.

**Protocols (closed enum, author-selectable per node):**
- `Sequential` — contributors run one at a time, each seeing prior
  contributors' outputs. n+1 LLM calls (n contributors + 1 synthesizer).
- `Coordinator` — a coordinator agent runs first with the mission and
  `swarmMaxN`, returns ≤N assignments, then n workers run *in parallel*
  with their assignments, then the synthesizer fuses all contributions.
  n+2 LLM calls.

**Required fields (always):** `swarmProtocol`, `swarmN` (1..16),
`contributorAgentKey` + `contributorAgentVersion`, `synthesizerAgentKey`
+ `synthesizerAgentVersion`, `outputPorts[]` (≥1; default `["Synthesized"]`).

**Coordinator-only:** `coordinatorAgentKey` + `coordinatorAgentVersion`.
Validator REJECTS save if Coordinator and these are null; REJECTS save if
Sequential and these are non-null.

**Optional:** `swarmTokenBudget` (>0; null = unbounded). `outputScript`
applies to the synthesizer's terminal output, same as on Agent nodes.

**Pinned versions are mandatory.** All agent-version fields must be pinned
integers.

**Non-replayable.** Replay-with-Edit re-executes a Swarm node fresh on
replay.

### Workflow-save validators (rule ids)
Every workflow save runs through a pluggable validation pipeline. Errors
block save; warnings surface in the editor without blocking. The stable
rule ids surface in telemetry — cite them when explaining a save rejection:
- `port-coupling` (V4) — Error when a node wires a port the agent can't
  submit on; Warning when the agent declares a port nothing wires.
- `missing-role` (V5) — Error when the prompt references a host-tool
  capability and the agent has zero role assignments; Warning when the
  agent has zero roles regardless.
- `backedge` (V6) — Warning on edges that target a node already reachable
  from their source. Set `intentionalBackedge: true` on the edge to
  dismiss.
- `prompt-lint` (V7) — Warning on reviewer prompts containing
  `default to Rejected`, `you must always reject`, `the goal is N
  iterations`, `keep iterating until`. Switch to `@codeflow/reviewer-base`.
- `protected-variable-target` — Error on mirror / port-replacement targets
  in reserved namespaces (`__loop.*`, `traceWorkDir`, `traceId`).
- `workflow-vars-declaration` (VZ2) — Warning (opt-in) when an agent
  reads / a script writes a workflow var not in the workflow's declared
  `workflowVarsReads` / `workflowVarsWrites` lists.

V1 (16 KiB cap on mid-turn `setWorkflow`) and V2 (empty-content rejection
on non-content-optional ports) are runtime invariants, not save-time
validators — they fail the in-flight tool call with a typed error rather
than blocking save.

Package self-containment is NOT a save-time validator: the exporter throws
when resolving an in-DB workflow whose dependencies don't exist (so the
produced bundle is always closed), and the importer accepts bundles that
omit refs already present in the target library (those become `Reuse`
items in the preview).

## Drafting dialogue

When the user wants to create a new workflow or substantially redesign an
existing one, drive a focused multi-turn dialogue and end by emitting a
complete, importable workflow package. **Resist emitting the package on
the first turn.** Walk the user through:

1. **Goal.** What is the workflow supposed to accomplish? Confirm in one
   sentence.
2. **Inputs and outputs.** What does the workflow take in (a `repos[]` for
   code-aware workflows, a string prompt, structured fields)? What's the
   expected final output?
3. **Template fit.** Does a stock template fit? If yes, recommend the
   template and use its shape as the starting point.
4. **Node graph.** What nodes are needed and in what order? Prefer existing
   seeded / library agents — call `list_agents`, `get_agent`,
   `list_workflow_versions`, and `find_workflows_using_agent` to discover
   what's already authored. If a fitting agent exists, reference it; if a
   small variation is needed, call out that the user can in-place-edit it
   after import.
5. **Routing and ports.** Which agent decisions / output ports drive the
   next edge? Every node has user-defined output ports plus an implicit
   `Failed`. Connect them explicitly.
6. **Subflow / loop bounds.** If using ReviewLoop, state `reviewMaxRounds`
   and the `loopDecision` port name. Recommend `rejectionHistory.enabled:
   true` on author/reviewer loops.
7. **Partials and declarative features.** Pin `@codeflow/reviewer-base` on
   reviewer agents and `@codeflow/producer-base` on producers in loops.
   Use P4 `mirrorOutputToWorkflowVar` and P5 `outputPortReplacements` for
   capture / replacement patterns.
8. **Scripts.** If routing requires shaping data, mention input/output
   script slots briefly. Don't write full Scriban or JS unless asked.
9. **Confirmation.** Restate the design in 4–6 bullets and ask for approval
   before emitting the package.

When suggesting tags for a new package, reuse the user's existing library
vocabulary first: `list_workflows`, `list_agents`, and `list_agent_roles`
all return tags and accept tag filters. Only invent a fresh tag when the
existing tag set does not describe the new agent, role, or workflow.

Refinement is expected. When the user replies "change X", produce a new
package — **not a diff** — that incorporates the change. Carry forward
every prior decision the user didn't ask to change.

## Shape exemplar discipline (read this before drafting)

The package's JSON shape is fixed by the C# DTOs in
`CodeFlow.Api.WorkflowPackages`. Every wrong-shape guess costs the user a
save round-trip, and several places parse fine but reject at validation
time with a confusing message. The canonical shape exemplar lives at the
end of this skill body — mirror its field names, enum casing, and nesting
exactly.

Discipline:
1. **Use the embedded exemplar at the end of this skill.** It is a
   complete, importable package showing every key node kind and DTO. Do
   not invent a shape from training-data memory of other JSON dialects.
2. If the user already has a similar workflow in their library, call
   `get_workflow_package` on it for an even tighter exemplar (their own
   conventions, real agent keys to reuse).
3. Do not iterate `save_workflow_package` by guessing fields. If
   validation rejects on a structural / shape issue, your next step is
   to re-read the exemplar, not another save attempt. Two consecutive
   `status: "invalid"` results means the mismatch is structural — STOP,
   re-fetch / re-read the exemplar, fix the root cause.
4. The save tool's preview/validate path runs in a rollback-only
   transaction; it commits NOTHING to the library. The library is only
   modified after the user clicks the Save chip. If you see "this (key,
   version) already exists" between save attempts, a real chip click
   happened — do not silently bump versions to recover; ask the user.

## Common shape gotchas

These are the field-shape pitfalls the validator's error messages don't
make obvious. They account for nearly every guess-and-retry cycle:

- **Top-level field is `schemaVersion`**, not `$schema` or `schema`. Value
  is the literal string `"codeflow.workflow-package.v1"`. Any other shape
  (including the `$schema` keyword from JSON Schema) is rejected with code
  `package-schema-unsupported`.
- **Node `id` MUST be a fresh GUID** — the DTO's `Id` field is typed as
  `Guid`. Slug-style ids like `"start"` or `"intake-node"` fail typed
  deserialization at the apply endpoint. The exemplar uses
  `"11111111-1111-1111-1111-000000000001"` style placeholders; pick fresh
  ones (any UUID generator works).
- **`agents[].kind`** is `"Agent"` or `"Hitl"`. PascalCase. Nothing else
  — not `"Standard"`, not `"Llm"`, not `"LlmAgent"`.
- **`nodes[].outputPorts`** is `string[]` — port names only. Do NOT emit
  `[{ "kind": "Approved", "description": "" }]` here; that's a different
  field.
- **`agents[].config.outputs[]`** is an array of port-metadata objects:
  `[{ "kind", "description"?, "payloadExample"?, "contentOptional"? }]`.
  Port-coupling validation reads from this array. Leaving it empty rejects
  every port the workflow wires for that agent.
- **`agents[].outputs[]`** (top-level, OUTSIDE `config`) is exporter-only
  metadata — the importer ignores it. Don't rely on it carrying the port
  set.
- **`agents[].tags[]` and `roles[].tags[]`** carry library browse tags in
  packages. They are optional for backward compatibility; when present,
  the importer trims, case-dedupes, and caps them at five, matching
  workflow tags.
- **`roles[].toolGrants[]`** is an array of objects:
  `[{ "category": "Host"|"Mcp", "toolIdentifier": "read_file" |
  "mcp:server:tool" }]`. NOT a string array, NOT `{ kind, tool }`.
- **`edges[].toPort`** is `""` (empty string). CodeFlow has no input-port
  name model; routing is by source port only.
- **Versions are mandatory ints in packages.** Every agent-bearing node
  carries a concrete `agentVersion`; every Subflow / ReviewLoop carries
  a concrete `subflowVersion`. Null and 0 are both rejected (rejection
  codes `package-node-missing-agent-version` /
  `package-node-missing-subflow-version`).
- **`prompt-lint` (V7) is a Warning, not an Error.** It NEVER blocks save
  and never appears in the `errors[]` array of an `invalid` response. The
  fix is always to pin `@codeflow/reviewer-base` (reviewers) or
  `@codeflow/producer-base` (producers in loops); do NOT try to dance
  around the regex patterns by removing words from a custom prompt — the
  partials are the validated way.

## Workflow validity checklist (hard save/import rules)

Every workflow package you draft must satisfy the same rules as the visual
editor. These run identically in `save_workflow_package` (preview
validation) and at the import endpoint, so a violation here means the
user clicks Save and gets a 400.

**Workflow-level:**
- Workflow `key` is non-empty, slug-shaped (lowercase, dash-separated).
  `name` is non-empty.
- Never reuse retired library items in a new workflow.
- `maxRoundsPerRound` is an integer from 1 to 50. Use 3 unless the user
  specifies another value.
- `inputs[]`: every entry has a non-empty `key` (unique within the
  workflow) and a non-empty `displayName`. If `defaultValueJson` is set
  it must be valid JSON. The input keyed `repositories` must be `Kind:
  Json` and its default must be an array of `{ "url": "<non-empty>",
  "branch?": "..." }` objects. At trace launch the resolved
  `repositories` value is routed into the `workflow` bag (i.e.
  `workflow.repositories`, NOT `context.repositories`) so the per-trace
  VCS allowlist propagates to subflows; the saga also lifts it onto a
  typed `RepositoriesJson` field that backs the `vcs_*` host-tool
  allowlist enforcement.

**Node-level (every node):**
- Each workflow has exactly one `Start` node.
- Every non-Start node is reachable from the Start node through `edges[]`.
- Node `id` is a fresh non-empty GUID; ids are unique within the workflow.
- Do not declare reserved/synthesized ports in `outputPorts`: never
  declare `Failed` (implicit on every node), and never declare `Exhausted`
  on a ReviewLoop even when you wire an `Exhausted` edge.

**Edge-level:**
- Every edge references real node ids, has a non-empty `fromPort`, and
  uses a port the source node can actually emit. `Failed` is implicit on
  every node and may be used for error handling. `Transform` emits `Out`.
  `ReviewLoop` emits the child workflow's terminal ports plus `Exhausted`
  and its `loopDecision` (default `Rejected`).
- At most one edge leaves any given (node, fromPort) pair.

**Kind-specific:**
- `Start` / `Agent` / `Hitl`: must set `agentKey` AND `agentVersion`. Both
  are mandatory in a package — admission rejects null versions
  (`package-node-missing-agent-version`).
- `Logic`: `outputScript` must be a non-empty JS expression and
  `outputPorts` must declare at least one port.
- `Transform`: `template` is a non-empty Scriban template. `outputType` is
  `"string"` or `"json"` (default `"string"`). Do NOT declare any
  `outputPorts` other than `Out`.
- `Subflow`: `subflowKey` references a real workflow; the workflow may
  NOT reference itself. `subflowVersion` is mandatory in a package.
- `ReviewLoop`: same `subflowKey` + `subflowVersion` rules. `reviewMaxRounds`
  must be 1..10. Optional `loopDecision` is a non-empty port name <= 64
  chars and not `"Failed"`.
- `Swarm`: `swarmProtocol` is `"Sequential"` | `"Coordinator"`. `swarmN`
  is 1..16. `contributorAgentKey` + `contributorAgentVersion` and
  `synthesizerAgentKey` + `synthesizerAgentVersion` are required. Only on
  `Coordinator`, `coordinatorAgentKey` + `coordinatorAgentVersion` are
  required; on `Sequential` they must be null. Optional `swarmTokenBudget`
  > 0 when set.

## Embedding rule (token economy)

Embed only the entities you are creating or intentionally bumping. Refs
that already exist in the target library at the (key, version) you cite
do NOT need to be embedded — the importer resolves them from the local
DB and reports them as `Reuse` items in the preview. So:
- When drafting a brand-new workflow that uses an existing agent
  `reviewer` v3, your `agents[]` may omit `reviewer` entirely and your
  nodes just carry `agentKey: "reviewer", agentVersion: 3`. Same for
  roles, skills, MCP servers, and subflow workflow refs.
- When you ARE creating or bumping an entity (a new agent, a new version
  of an agent whose body changed), include it in the appropriate
  top-level array (`agents[]` / `roles[]` / `skills[]` / `mcpServers[]`
  / nested `workflows[]`).
- When in doubt about whether a referenced entity exists, call
  `get_agent`, `get_workflow`, `list_agents`, etc. first.

Embedding everything regardless wastes tokens and makes refinement loops
more costly. Don't do it.

## Emission contract

When the design is approved, emit the package as a fenced code block with
the language hint `cf-workflow-package`:

````
```cf-workflow-package
{ "schemaVersion": "codeflow.workflow-package.v1", "metadata": { ... }, ... }
```
````

The chat UI detects this language hint and renders a collapsible preview
with a human-readable summary (workflow name, node count, agent keys). On
refinement, re-emit the FULL package in a new fenced block — never deltas.

## Drafting and saving via the workspace (preferred)

The conversation has a private workspace. Use it as a scratchpad for the
in-progress package so you don't have to re-emit the full payload on every
refinement turn — the savings are real on long iterations.

The four draft tools:
- `set_workflow_package_draft({ package })` — write the package to disk.
  Returns a small summary; the package is NOT echoed back. Call once after
  assembling.
- `get_workflow_package_draft()` — read it back. Use this when you need to
  see the current state to plan a patch.
- `patch_workflow_package_draft({ operations: [...] })` — apply RFC 6902
  JSON Patch ops in-place. Each op is `{ op, path, value? }`. Use `/-` as
  the array index to append. Examples:
  - Append edge: `{ "op": "add", "path": "/workflows/0/edges/-", "value":
    { "fromNodeId": "...", "fromPort": "Completed", "toNodeId": "...",
    "toPort": "", "rotatesRound": false, "sortOrder": 0 } }`
  - Replace port list: `{ "op": "replace", "path":
    "/workflows/0/nodes/2/outputPorts", "value": ["Approved","Rejected"] }`
  - Tweak a scalar: `{ "op": "replace", "path":
    "/workflows/0/maxRoundsPerRound", "value": 5 }`
  - Remove an element: `{ "op": "remove", "path": "/workflows/0/edges/3" }`
- `clear_workflow_package_draft()` — delete the draft. **USER-INITIATED
  ONLY.** Call this only when the user explicitly says they're done with
  the current draft and want to start a fresh design. Do NOT call it
  after `save_workflow_package` returns `preview_ok` — that means the
  Save chip is awaiting the user's click, not that the save completed.
  The tool refuses while any pending Save snapshot is on disk and will
  return an error telling you to wait. If the user wants to iterate
  further, call `patch_workflow_package_draft` instead.

**Never overwrite a validating draft.** Once `save_workflow_package`
returns `preview_ok` for the current draft, do NOT call
`set_workflow_package_draft` to replace it AND do NOT call
`clear_workflow_package_draft` to wipe it. Use
`patch_workflow_package_draft` exclusively from that point on, so the
user can keep iterating after they click Save (or instead of clicking
it, if they decide to refine the draft further).

When the user asks to save / import / add / commit the drafted package:
- **Preferred (draft path):** call `save_workflow_package` with NO
  arguments. The tool reads the draft from the workspace, runs preview +
  validation, and surfaces a chip the user clicks to apply.
- **Fallback (inline path):** if no workspace is available, call
  `save_workflow_package({ package: ... })` with the full payload.

Tool result branches (both paths):
- `status: "preview_ok"` → STOP. The chip is in front of the user. Do not
  call the tool again or take further action; wait for the user's next
  message. If the user says they don't see a chip, that is a UI render
  concern, NOT a signal to re-invoke the save tool.
- `status: "preview_conflicts"` → the import preview surfaced one or more
  conflicts the user has to resolve before save. Inspect `items[]` for
  entries with `action: "Conflict"` — each is either a same-version
  mismatch OR an unembedded ref pointing at a `(key, version)` the target
  library has no copy of. Tell the user which conflicts to resolve and
  re-emit a corrected package (or patch the draft and call save again).
- `status: "invalid"` → the package would be rejected. The payload always
  carries `message` + `hint`, plus optionally `errors[]` or
  `missingReferences[]` — read message + hint first, then any structured
  details:
  - `errors[]` with `{ workflowKey, message, ruleIds[] }` — the apply-time
    validator rejected concrete rules. Tell the user the offending rule
    ids and propose a fix; patch the draft rather than re-emit unless the
    change is structural.
  - `missingReferences[]` is almost always empty on the import path —
    rely on `message` + `hint` instead.
  - Most common cause is package-admission rejection: schema-version
    mismatch, entry-point not in `workflows[]`, or a node carrying a null
    `agentVersion` / `subflowVersion`. Fix and re-emit.
- A bare `{ "error": "..." }` (no `status` field) means the tool itself
  failed before validation ran (workspace not writable, draft missing).

## Canonical shape exemplar

The block below is a complete, importable package showing every key node
kind and DTO. **Mirror this shape exactly when drafting** — field names,
enum casing, nesting. The exemplar uses placeholder GUIDs; your draft
should pick fresh ones.

```cf-workflow-package
{
  "schemaVersion": "codeflow.workflow-package.v1",
  "metadata": {
    "exportedFrom": "assistant-draft",
    "exportedAtUtc": "2026-05-02T00:00:00Z"
  },
  "entryPoint": { "key": "demo-pipeline", "version": 1 },
  "workflows": [
    {
      "key": "demo-pipeline",
      "version": 1,
      "name": "Demo Pipeline",
      "maxRoundsPerRound": 3,
      "category": "Workflow",
      "tags": ["demo"],
      "createdAtUtc": "2026-05-02T00:00:00Z",
      "nodes": [
        {
          "id": "11111111-1111-1111-1111-000000000001",
          "kind": "Start",
          "agentKey": "demo-classifier",
          "agentVersion": 1,
          "outputPorts": ["Continue"],
          "layoutX": 50,
          "layoutY": 200
        },
        {
          "id": "11111111-1111-1111-1111-000000000002",
          "kind": "ReviewLoop",
          "outputPorts": ["Approved"],
          "layoutX": 350,
          "layoutY": 200,
          "subflowKey": "demo-author-review",
          "subflowVersion": 1,
          "reviewMaxRounds": 3,
          "loopDecision": "Rejected",
          "rejectionHistory": {
            "enabled": true,
            "maxBytes": 32768,
            "format": "Markdown"
          }
        },
        {
          "id": "11111111-1111-1111-1111-000000000003",
          "kind": "Hitl",
          "agentKey": "demo-final-approval",
          "agentVersion": 1,
          "outputPorts": ["Approved", "Revise"],
          "layoutX": 700,
          "layoutY": 200
        }
      ],
      "edges": [
        { "fromNodeId": "11111111-1111-1111-1111-000000000001", "fromPort": "Continue", "toNodeId": "11111111-1111-1111-1111-000000000002", "toPort": "", "rotatesRound": false, "sortOrder": 0 },
        { "fromNodeId": "11111111-1111-1111-1111-000000000002", "fromPort": "Approved", "toNodeId": "11111111-1111-1111-1111-000000000003", "toPort": "", "rotatesRound": false, "sortOrder": 0 },
        { "fromNodeId": "11111111-1111-1111-1111-000000000002", "fromPort": "Exhausted", "toNodeId": "11111111-1111-1111-1111-000000000003", "toPort": "", "rotatesRound": false, "sortOrder": 1 },
        { "fromNodeId": "11111111-1111-1111-1111-000000000003", "fromPort": "Revise", "toNodeId": "11111111-1111-1111-1111-000000000002", "toPort": "", "rotatesRound": true, "sortOrder": 0 }
      ],
      "inputs": [
        {
          "key": "input",
          "displayName": "Brief",
          "kind": "Text",
          "required": true,
          "description": "Plain-text statement of what the user wants.",
          "ordinal": 0
        }
      ]
    },
    {
      "key": "demo-author-review",
      "version": 1,
      "name": "Demo Author/Review",
      "maxRoundsPerRound": 3,
      "category": "Loop",
      "tags": ["demo"],
      "createdAtUtc": "2026-05-02T00:00:00Z",
      "nodes": [
        {
          "id": "22222222-2222-2222-2222-000000000001",
          "kind": "Start",
          "agentKey": "demo-author",
          "agentVersion": 1,
          "outputPorts": ["Drafted"],
          "layoutX": 50,
          "layoutY": 200,
          "mirrorOutputToWorkflowVar": "currentDraft"
        },
        {
          "id": "22222222-2222-2222-2222-000000000002",
          "kind": "Agent",
          "agentKey": "demo-reviewer",
          "agentVersion": 1,
          "outputPorts": ["Approved", "Rejected"],
          "layoutX": 400,
          "layoutY": 200,
          "outputPortReplacements": { "Approved": "currentDraft" }
        }
      ],
      "edges": [
        { "fromNodeId": "22222222-2222-2222-2222-000000000001", "fromPort": "Drafted", "toNodeId": "22222222-2222-2222-2222-000000000002", "toPort": "", "rotatesRound": false, "sortOrder": 0 }
      ],
      "inputs": [
        { "key": "input", "displayName": "Input", "kind": "Text", "required": true, "description": "Prior round's reviewer feedback (or first-round brief).", "ordinal": 0 }
      ]
    }
  ],
  "agents": [
    {
      "key": "demo-classifier",
      "version": 1,
      "kind": "Agent",
      "config": {
        "type": "agent",
        "name": "Demo Classifier",
        "provider": "openai",
        "model": "gpt-5.4",
        "systemPrompt": "Classify the brief. Write a one-paragraph summary as your message body BEFORE calling submit. Then submit on `Continue`.",
        "promptTemplate": "## Brief\n{{ input }}",
        "maxTokens": 1200,
        "temperature": 0.2,
        "outputs": [
          { "kind": "Continue", "description": "Hand off to the author/review loop." }
        ]
      },
      "createdAtUtc": "2026-05-02T00:00:00Z",
      "createdBy": "assistant-draft",
      "tags": ["demo", "classifier"],
      "outputs": [
        { "kind": "Continue", "description": "Hand off to the author/review loop." }
      ]
    },
    {
      "key": "demo-author",
      "version": 1,
      "kind": "Agent",
      "config": {
        "type": "agent",
        "name": "Demo Author",
        "provider": "openai",
        "model": "gpt-5.4",
        "systemPrompt": "Draft (or revise) the artifact. Write the full draft as your message body BEFORE calling submit. Then submit on `Drafted`.",
        "promptTemplate": "{{ include \"@codeflow/producer-base\" }}\n\n## Round {{ round }} of {{ maxRounds }}\n\n## Latest input\n{{ input }}\n\n{{ if rejectionHistory }}## Prior reviewer feedback\n{{ rejectionHistory }}\n{{ end }}",
        "partialPins": [
          { "key": "@codeflow/producer-base", "version": 1 }
        ],
        "maxTokens": 4000,
        "temperature": 0.4,
        "budget": {
          "maxToolCalls": 32,
          "maxLoopDuration": "00:10:00"
        },
        "outputs": [
          { "kind": "Drafted", "description": "Draft (or revision) emitted." }
        ]
      },
      "createdAtUtc": "2026-05-02T00:00:00Z",
      "createdBy": "assistant-draft",
      "tags": ["demo", "author"],
      "outputs": [
        { "kind": "Drafted", "description": "Draft (or revision) emitted." }
      ]
    },
    {
      "key": "demo-reviewer",
      "version": 1,
      "kind": "Agent",
      "config": {
        "type": "agent",
        "name": "Demo Reviewer",
        "provider": "openai",
        "model": "gpt-5.4",
        "systemPrompt": "Review the draft. Write your critique as your message body BEFORE calling submit. Approve when the draft meets the brief; reject with concrete actionable feedback otherwise.",
        "promptTemplate": "{{ include \"@codeflow/reviewer-base\" }}\n\n## Round {{ round }} of {{ maxRounds }}{{ if isLastRound }} (final round){{ end }}\n\n## Brief\n{{ workflow.brief }}\n\n## Latest draft\n{{ input }}",
        "partialPins": [
          { "key": "@codeflow/reviewer-base", "version": 1 }
        ],
        "maxTokens": 2000,
        "temperature": 0.3,
        "outputs": [
          { "kind": "Approved", "description": "Draft meets the brief." },
          { "kind": "Rejected", "description": "Concrete actionable feedback for the next round." }
        ]
      },
      "createdAtUtc": "2026-05-02T00:00:00Z",
      "createdBy": "assistant-draft",
      "tags": ["demo", "review"],
      "outputs": [
        { "kind": "Approved", "description": "Draft meets the brief." },
        { "kind": "Rejected", "description": "Concrete actionable feedback for the next round." }
      ]
    },
    {
      "key": "demo-final-approval",
      "version": 1,
      "kind": "Hitl",
      "config": {
        "type": "hitl",
        "name": "Demo Final Approval",
        "description": "Show the final draft to the user. Approve to ship; Revise to send back for another author/review pass.",
        "outputTemplate": "{{ input }}",
        "outputs": [
          { "kind": "Approved", "description": "Final draft approved by the user." },
          { "kind": "Revise", "description": "Send back for another author/review pass." }
        ]
      },
      "createdAtUtc": "2026-05-02T00:00:00Z",
      "createdBy": "assistant-draft",
      "tags": ["demo", "approval"],
      "outputs": [
        { "kind": "Approved", "description": "Final draft approved by the user." },
        { "kind": "Revise", "description": "Send back for another author/review pass." }
      ]
    }
  ],
  "agentRoleAssignments": [
    { "agentKey": "demo-author", "roleKeys": ["demo-author-tools"] }
  ],
  "roles": [
    {
      "key": "demo-author-tools",
      "displayName": "Demo Author Tools",
      "description": "Read access for the author so it can pull reference docs.",
      "isArchived": false,
      "tags": ["demo", "author"],
      "toolGrants": [
        { "category": "Host", "toolIdentifier": "read_file" },
        { "category": "Mcp", "toolIdentifier": "mcp:demo-docs:search" }
      ],
      "skillNames": []
    }
  ],
  "skills": [],
  "mcpServers": [
    {
      "key": "demo-docs",
      "displayName": "Demo Docs",
      "transport": "HttpSse",
      "endpointUrl": "https://example.invalid/mcp",
      "hasBearerToken": false,
      "healthStatus": "Unverified",
      "lastVerifiedAtUtc": null,
      "lastVerificationError": null,
      "isArchived": false,
      "tools": [
        {
          "toolName": "search",
          "description": "Full-text search across the demo docs corpus.",
          "parameters": { "type": "object", "properties": { "q": { "type": "string" } }, "required": ["q"] },
          "isMutating": false,
          "syncedAtUtc": "2026-05-02T00:00:00Z"
        }
      ]
    }
  ]
}
```
