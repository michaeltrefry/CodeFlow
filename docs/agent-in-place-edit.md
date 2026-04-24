# In-place agent editing

When you're tuning an agent to fit a specific workflow — reshaping its outputs to
match the workflow's edges, tweaking its prompt for a new context, experimenting
with decision templates — bouncing between the agent editor page and the workflow
canvas gets in the way. The in-place editor lets you edit a node's agent right on
the canvas without contaminating the same-keyed agent in every other workflow
that uses it.

## Mental model

- **Right-click a node** (Agent, HITL, Start, or Escalation) and pick
  **Edit agent in place…**.
- The first time you do this in a workflow editing session, you'll see a
  confirmation that explains: saving creates a **workflow-scoped fork**, the
  original agent is untouched, and you can publish the fork back later.
- Closing or cancelling the modal **does not** create anything on the server.
  The fork is minted only when you click save.
- On save, the node re-links to the fork (`__fork_…` key). The workflow still
  needs to be saved for the re-link to persist.

## Visual indicators

- Forked nodes show a small violet `fork` badge next to the node title.
- Inside the edit modal, a "scoped to this workflow" chip reminds you the
  changes won't affect other consumers of the original agent.

## Publishing back

Once you're happy with the fork and want to share it with the wider library,
right-click the forked node again and pick **Publish fork…**. The dialog
shows:

- The agent you forked from and the version.
- The original agent's current latest version.
- A **drift** warning if the original has moved forward since the fork was
  taken.

You can either:

1. **Publish to the original** — creates a new version of the forked-from
   agent with your fork's config. If there's drift, the publish requires an
   explicit acknowledgement that you're overwriting newer edits upstream.
2. **Publish as a new agent** — creates a brand-new library agent with your
   fork's config under a new key. Safer when drift exists.

Either way the node auto-relinks to the published target, the fork badge
disappears, and the fork row stays in the DB for lineage tracking.

## Non-goals today

- **No automatic merging** of drift between the fork and the upstream. The
  publish dialog is binary: overwrite (with ack) or publish-as-new.
- **No undo** of an in-place edit once saved. To revert, pick the original
  agent back in the inspector sidebar and re-save the workflow.
- **No cross-workflow reuse** of a fork. A fork belongs to the workflow it was
  created in; re-using the config elsewhere requires publishing first.

## API surface

| Method & path | Purpose |
|---|---|
| `POST /api/agents/fork` | Mint a workflow-scoped fork from a source `(key, version)`. |
| `PUT /api/agents/{forkKey}` | Add a new version to an existing fork. |
| `GET /api/agents/{forkKey}/publish-status` | Drift lineage + current original latest. |
| `POST /api/agents/{forkKey}/publish` | Publish back — `mode: "original" \| "new-agent"`, optional `acknowledgeDrift`, optional `newKey`. |

Forks never appear in `GET /api/agents`.

## Data model

Forks are normal `agent_configs` rows with three extra columns:

- `owning_workflow_key` — non-null marks the row as workflow-scoped.
- `forked_from_key` — the key the fork was copied from.
- `forked_from_version` — the version that was copied.

Published agents carry `forked_from_*` lineage on their new row too (for
traceability), but `owning_workflow_key` is `NULL` so they show up in the
library.
