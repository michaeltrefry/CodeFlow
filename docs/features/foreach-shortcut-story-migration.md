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

`shortcut-story-end-to-end`'s node 4 changes from a `ReviewLoop` to a `ForEach`. The `subflowKey` stays — the same `shortcut-development-task-loop` workflow handles one iteration.

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

Per the [`feedback_workflows_are_data`](../../README.md) convention, the agent prompts, workflow definitions, and per-node configs all live as data in the running CodeFlow instance — not as source code in this repo. There is nothing to commit to the codebase to "implement" FE-6 directly; the platform changes the iteration depends on (FE-1 through FE-5) are already merged, and this playbook is the operational guide for applying them to the motivating workflow.

The user runs Steps 1 through 3 via the workflow / agent editor (or the chat-panel's `save_workflow_package` tool, which carries the per-agent prompt edits in a single package and handles the version bumps atomically). Steps 4 and 5 require a live Shortcut story to operate against.
