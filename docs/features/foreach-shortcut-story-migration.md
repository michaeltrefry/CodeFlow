# Migration playbook: reshape `shortcut-story-end-to-end` to use the ForEach node

**Story:** [sc-947 / FE-6](https://app.shortcut.com/trefry/story/947) — final slice of the [ForEach iteration node-kind epic](https://app.shortcut.com/trefry/epic/941).

## Why this migration exists

The `shortcut-story-end-to-end` workflow's node 4 today is a `ReviewLoop` wrapping `shortcut-development-task-loop`. The prep agent emits a prose "Suggested execution order"; the dev agent consumes the whole prose handoff inside a single saga and tries to walk every layer in one tool-call budget. On real stories with 3–5 task decompositions this fails reliably under tool-call budget pressure regardless of model size — trace `1efaba0d-69ee-4008-99b3-5859b72ebff3` hit `ToolCallBudgetExceeded` after 51 tool calls before any committable work.

The fix is structural: each task gets its own dev → reviewer cycle with a fresh tool-call budget, a clean transcript, and a tighter prompt. That's what the new `ForEach` node ([sc-941 epic](https://app.shortcut.com/trefry/epic/941), shipped via FE-1 through FE-5) provides at the platform level. This playbook is the per-workflow migration that wires the existing prep / dev / reviewer agents into that shape.

## Prerequisites

All of FE-1 through FE-5 must be live in the target CodeFlow instance:

| Slice | What it adds | How to verify |
|---|---|---|
| FE-1 ([#353](https://github.com/michaeltrefry/CodeFlow/pull/353)) | `WorkflowNodeKind.ForEach` enum + entity columns | `SHOW COLUMNS FROM workflow_nodes LIKE 'collection_expression';` returns a row |
| FE-2 ([#354](https://github.com/michaeltrefry/CodeFlow/pull/354)) | Dispatcher + saga lifecycle | A `ForEach` node actually dispatches a child saga at runtime |
| FE-3 ([#355](https://github.com/michaeltrefry/CodeFlow/pull/355)) | Validator rules + port synthesis | Saving a `ForEach` node with a bad `itemVar` is rejected with a clear error |
| FE-4 ([#357](https://github.com/michaeltrefry/CodeFlow/pull/357)) | Angular palette + inspector | The "For Each" palette button is visible in the workflow canvas |
| FE-5 ([#356](https://github.com/michaeltrefry/CodeFlow/pull/356)) | `ForEachIterationTemplate` + library example | "ForEach iteration" appears under the Other category in the template picker |

## The fast path: import the migration package

Most of the work below is bundled into [`workflows/shortcut-story-foreach-migration-v1-package.json`](../../workflows/shortcut-story-foreach-migration-v1-package.json). Importing the package atomically creates **new versions** of the three agents + the child workflow (the importer auto-bumps because the keys already exist in your instance, and it rewrites cross-refs to the new versions on bump). The only step left after import is to edit node 4 of your real `shortcut-story-end-to-end` workflow on the canvas — that's Step 2 below.

```bash
# From the chat panel, drag the file in and submit, or via curl:
curl -X POST https://<your-codeflow>/api/workflows/package/apply \
    -H 'Content-Type: application/json' \
    -d "{\"package\": $(cat workflows/shortcut-story-foreach-migration-v1-package.json)}"
```

The package ships:

| Entity | Role | Key | Notes |
|---|---|---|---|
| `shortcut-implementation-prep-agent` | Reshaped prompt; enumerates workspace before emitting the task list; output emits `workflow.implementationTasks` | unchanged | Bumped on import |
| `shortcut-task-paths-validator-agent` | New: backstop that verifies every task's `files[]` entries exist before ForEach dispatches | **new** | Created on first import; bumped on re-import |
| `shortcut-developer-agent` | Reads `loop.item.*` for one-task scope | unchanged | Bumped on import |
| `shortcut-code-reviewer-agent` | Reviews against `loop.item.constraints` | unchanged | Bumped on import |
| `shortcut-development-task-loop` (workflow) | Start (dev) → reviewer, Approved/Rejected ports | unchanged | Bumped on import |
| `shortcut-code-worker` (role) | Tool grants shared by all four agents | new | Self-contained per package admission rules |

Your real parent workflow (`shortcut-story-end-to-end`) is **intentionally not** in the package — it has additional intake / plan / completion / post-mortem nodes the FE-6 card explicitly leaves untouched. The canvas edits (one new validator node + the node-4 ReviewLoop→ForEach swap) are fast now that the palette ships a "For Each" button (FE-4 / [#357](https://github.com/michaeltrefry/CodeFlow/pull/357)).

### Why the validator agent exists

The first end-to-end run of the reshape failed because the prep agent hallucinated paths (trace `7167cf25-...`) — its task list referenced files in a `src/Azimuth.Workflows.Core/` namespace that doesn't exist in the CodeFlow repo. The platform machinery (`loop.item.*` propagation, output-script JSON parse, ForEach first-failure-aborts) all worked correctly; the failure was purely the LLM inventing paths. Two reinforcing fixes shipped:

1. **Self-discovery in the prep agent (Option 2).** The prep agent's prompt now requires `run_command ['find', '.', '-type', 'f', ...]` enumeration BEFORE emitting any task. Every `files` entry must be grounded in a path that actually appears in the discovery output, or explicitly flagged as a create-new file in the task's `constraints`.
2. **Path validator backstop (Option 4).** The new `shortcut-task-paths-validator-agent` sits between prep and ForEach. It re-runs discovery, walks every task's `files[]`, and routes `Invalid` (→ post-mortem) if any path is missing AND not flagged as a create-new. Defense-in-depth — catches mistakes cheaply, before the first iteration burns a tool-call budget hunting for nonexistent files.

In the parent workflow's wiring, the validator slots in like this:

```
... → prep.Continue → validator.Valid → ForEach → completion → post-mortem → final-hitl
                    ↘ validator.Invalid → post-mortem (so the missing paths surface in the report)
```

If you'd prefer to apply the prompt deltas to your existing agents by hand instead of taking the package's prompt verbatim, the per-prompt addendums below give you the structural pieces; pair them with whatever prompt engineering your existing agents already have.

## Migration overview

Three artifacts change. Bump each independently — the parent workflow's pinned versions pick up the new agents on the next save.

1. **`shortcut-implementation-prep-agent`** — extend the prompt to also emit `setWorkflow('implementationTasks', [...])` alongside the existing prose handoff. Bump version.
2. **`shortcut-story-end-to-end`** — replace node 4 (the `ReviewLoop` wrapping `shortcut-development-task-loop`) with a `ForEach` node over `workflow.implementationTasks`. Bump version.
3. **`shortcut-developer-agent`** and **`shortcut-code-reviewer-agent`** — change input expectation from "the full prep handoff" to "one task object via `loop.task`." Bump each version.

The dev / reviewer pair workflow (`shortcut-development-task-loop`) itself stays the same shape — it's a good unit of work for one task. Only the prompts inside it shift.

## Step 1 — Reshape the prep agent

The prep agent today emits a single prose section. The migration teaches it to ALSO emit a structured JSON array via the existing `setWorkflow` output-script primitive.

**Required output shape** (each item is one layer-scoped task):

```json
{
  "name": "string — short label, e.g. 'Add validator rule'",
  "scope": "string — files / module the work touches, e.g. 'CodeFlow.Api/Validation'",
  "files": ["string — relative path", "..."],
  "constraints": "string — explicit invariants the dev must honor"
}
```

**Prompt addendum** (append to the existing prep system prompt, do not replace it):

```
## Structured task list (in addition to the prose handoff)

After you finish the "Suggested execution order" prose section, emit a structured array
of tasks via setWorkflow so the downstream ForEach node can iterate one task at a time.
The prose stays — downstream context still benefits from it. The structured array is the
machine-readable form.

For every layer in your suggested execution order, append one entry to the array:

  {
    "name":        "<short label of the layer's work>",
    "scope":       "<files / module the layer touches>",
    "files":       ["<relative path>", "..."],
    "constraints": "<invariants this layer must honor, e.g. 'do not change the public
                   API of WorkflowValidator.AllowedOutputPorts'>"
  }

Then in your output script, call:

  setWorkflow('implementationTasks', tasks);

Where `tasks` is the array you just built.

Order matters — items are processed in order, the dev agent for item N sees item N's
fields under {{ loop.item.* }} (the runtime binds the array entry under loop.item).
```

**Output-script change.** The prep agent's output script must call `setWorkflow('implementationTasks', tasks)` where `tasks` is parsed from the model's response. The cleanest shape:

```javascript
// Existing logic that extracts the prose handoff stays as-is.
// Parse the structured array the model wrote after the prose section.
const match = /```json\s*\n([\s\S]*?)\n```/.exec(output);
if (!match) {
  setNodePath('Failed');
  log('Prep agent did not emit a JSON task list — re-prompt or fall back to the prose-only path.');
  return;
}

let tasks;
try {
  tasks = JSON.parse(match[1]);
} catch (e) {
  setNodePath('Failed');
  log(`Prep agent's JSON task list did not parse: ${e.message}`);
  return;
}

if (!Array.isArray(tasks) || tasks.length === 0) {
  setNodePath('Failed');
  log('Prep agent emitted an empty or non-array task list.');
  return;
}

setWorkflow('implementationTasks', tasks);
setNodePath('Continue');
```

Bump the prep agent's version when saving.

## Step 2 — Reshape the parent workflow

`shortcut-story-end-to-end`'s node 4 changes from a `ReviewLoop` to a `ForEach`, and a new validator Agent node lands between prep (node 3) and the new ForEach (node 4). The `subflowKey` on the ForEach stays — the same `shortcut-development-task-loop` workflow handles one iteration.

**Before** (ReviewLoop config):

```yaml
kind: ReviewLoop
subflowKey: shortcut-development-task-loop
subflowVersion: <pinned>
reviewMaxRounds: <whatever the current cap is>
loopDecision: Rejected   # iterate on reviewer Rejected
outputPorts: [Approved]
```

**After** (ForEach config):

```yaml
kind: ForEach
subflowKey: shortcut-development-task-loop
subflowVersion: <bump to latest after Step 3 lands>
collectionExpression: workflow.implementationTasks
itemVar: task
outputPorts: []   # ForEach synthesizes Continue + implicit Failed
```

**Edge rewiring.**

| Source port (old) | Target | Source port (new) |
|---|---|---|
| `prep.Continue` → `ReviewLoop` (node 4) | reroute to validator | `prep.Continue` → `validator` |
| n/a | new edge | `validator.Valid` → `ForEach` (node 4) |
| n/a | new edge | `validator.Invalid` → post-mortem |
| n/a | new edge | `validator.Failed` → post-mortem (implicit Failed path) |
| `ReviewLoop.Approved` → next-node | same target | `ForEach.Continue` → next-node |
| `ReviewLoop.Exhausted` → next-node | same target | DELETED — ForEach doesn't synthesize Exhausted |
| `ReviewLoop.Failed` → recovery node | same target | `ForEach.Failed` → same recovery node (port name is identical, only the source-node kind changes) |

If the workflow currently routes `Exhausted` somewhere meaningful (e.g. a post-mortem branch for "ran out of rounds"), pick one: either route `Failed` to the same post-mortem (it now fires on the first-task failure), or add a downstream Logic node that inspects the aggregate output for partial completion. The card explicitly defers continue-on-error semantics to a follow-up epic, so first-failure-aborts is the v1 contract.

Bump the parent workflow's version.

## Step 3 — Reshape the dev + reviewer prompts

The dev / reviewer pair now runs once per task, not once per story. Update both prompts to consume `{{ loop.task.* }}` instead of the whole prose handoff. The runtime binds the array entry under `loop.item` (alias `loop.task` because we set `itemVar: task`).

**`shortcut-developer-agent` prompt addendum:**

```
## What you receive

You are processing ONE task from the prep agent's structured task list — not the whole
story. Your iteration context:

- Item:        {{ loop.item }}                      (full JSON of this task)
- Index:       {{ loop.index }} of {{ loop.count }} (0-based; loop.isLast is "true" on the final task)
- Task name:   {{ loop.item.name }}
- Scope:       {{ loop.item.scope }}
- Files:       {{ loop.item.files }}
- Constraints: {{ loop.item.constraints }}

You have a fresh tool-call budget for this task. Do not try to "save tools for later" —
the next task gets its own clean budget. Focus on completing THIS task to the constraints,
then submit.

(The full prose handoff from the prep agent is still available on {{ workflow.* }} keys if
you need cross-task context, but the default is to act task-local.)
```

**`shortcut-code-reviewer-agent` prompt addendum:**

```
## What you review

You are reviewing ONE task — not the whole story. The developer just produced an artifact
scoped to:

- Task name:   {{ loop.item.name }}
- Scope:       {{ loop.item.scope }}
- Files:       {{ loop.item.files }}
- Constraints: {{ loop.item.constraints }}

Accept on this task's criteria alone. Cross-task concerns ("did the developer remember to
update the README we mentioned in task 2?") belong to a downstream post-mortem, NOT to
this review. Submit Approved or Rejected.
```

Bump both agent versions. Then bump the parent workflow's `subflowVersion` on node 4 to point at the freshly-bumped `shortcut-development-task-loop` (or leave it null for "latest at save" if your team's preference is to track the latest).

## Step 4 — Smoke test

Pick a real Shortcut story with a clean 3–5 task decomposition. Avoid sprawling stories on the first run.

1. **Launch the reshaped workflow** against the story.
2. **Watch the trace inspector.** You should see:
   - Prep agent runs once, emits prose + JSON task list, routes Continue.
   - ForEach node spawns one child saga per task in order (3–5 in this example).
   - Each child saga has its own dev → reviewer cycle with the per-task loop.* bindings visible in the inspector's variable scope panel.
3. **Check the aggregate output.** The ForEach node's Continue terminal artifact is a JSON array of `{index, outputRef, port}` entries — one per iteration. The post-mortem node should be able to fetch each `outputRef` to read the per-task developer output.
4. **Verify token-budget independence.** Each child saga should report its own tool-call counter starting at 0 — none of the per-iteration sagas should hit `ToolCallBudgetExceeded` if the per-task scope is reasonable.

### First-failure-aborts test

Pick a story where the prep agent emits at least 3 tasks, and configure a deliberate failure on task 2 (e.g. set the developer's tool-call budget to 1 so the second task can't complete).

- The ForEach saga should route through `Failed` after task 2.
- The saga's `failure_reason` should contain `iteration 2/3` (per the FE-2 contract).
- Task 3 should never dispatch (no third child saga).
- Sink / post-mortem node should fire on the `Failed` recovery edge.

## Step 5 — Roll-back

If the smoke test surfaces a regression, the parent workflow's previous version is unaffected and can be re-pinned. The prep agent's previous version still emits the prose-only handoff and the older ReviewLoop-based parent still consumes it. So roll-back is:

1. Re-pin `shortcut-story-end-to-end` to the pre-migration version.
2. Leave the bumped prep + dev + reviewer agents in place — they're additive (still emit the prose section) and the older parent ignores `workflow.implementationTasks`.

## Documentation updates suggested

- Update [docs/workflows.md](../workflows.md) if it describes the canonical end-to-end workflow shape.
- Update [docs/authoring-workflows.md](../authoring-workflows.md) to reference this playbook as the canonical example of migrating a ReviewLoop-misused-for-iteration to a ForEach.
- Update the workflow's library README (if it ships one alongside the JSON package) to flag the breaking shape change.

## Why this slice is data-modeling rather than a code change

Per the [`feedback_workflows_are_data`](../../README.md) convention, the agent prompts, workflow definitions, and per-node configs all live as data in the running CodeFlow instance — not as source code in this repo. The platform changes the iteration depends on (FE-1 through FE-5) are already merged; this playbook (plus the shipped migration package) is the operational guide for applying them to the motivating workflow.

The migration-package import covers Steps 1 + 3 (all three agent prompts + the child workflow). Step 2 (the single ForEach swap on the parent workflow's node 4) is a canvas edit. Steps 4 and 5 require a live Shortcut story to operate against.
