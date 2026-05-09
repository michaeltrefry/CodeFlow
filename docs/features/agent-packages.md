# Agent packages

Self-contained `.cf-agent-package.json` bundles for round-tripping a
single agent — config, role assignments, granted tools / skills / MCP
servers — between CodeFlow workspaces or between the library and a
hand-authored draft.

Mirrors the workflow-package surface (export endpoint, importer with
per-row resolution, assistant authoring tools, artifact rail) one tier
down. Where workflow packages bundle a workflow plus every agent it
references, an agent package bundles **one** agent plus the role / skill
/ MCP closure required to recreate it.

## Why a separate package shape

A workflow package can carry the same payload — agents and roles ride
along under `agents[]` / `roles[]`. The narrower agent-package shape
exists for one reason: **scope**. When the user's intent is "edit this
one agent's prompt" or "fork this agent into a variant", the workflow-
package draft is too big a unit. The assistant burns tokens re-emitting
unrelated workflows + nodes + edges; the user's draft modal shows
fields they're not editing; the importer's preview lists rows that
aren't in scope. The agent-package surface narrows the problem to one
agent.

## Schema

```
schemaVersion: "codeflow.agent-package.v1"
metadata: { exportedFrom, exportedAtUtc }
entryPoint: { key, version }       — the agent the package is "about"
agents[]: WorkflowPackageAgent     — exactly one row in v1; field is a
                                     list so a future "agent preset"
                                     package can carry more
agentRoleAssignments[]: { agentKey, roleKeys[] }
roles[]: WorkflowPackageRole       — every role any included assignment
                                     references
skills[]: WorkflowPackageSkill     — every skill any included role grants
mcpServers[]: WorkflowPackageMcpServer
                                   — every MCP server any included role's
                                     `Mcp` toolGrants reference
manifest: { agent, roles[], skills[], mcpServers[] }
                                   — flat at-a-glance summary
```

Element records (`WorkflowPackageAgent`, `WorkflowPackageRole`, etc.)
are reused verbatim from the workflow-package shape so the importer can
share its per-agent / per-role / per-skill / per-MCP diff logic without
schema translation. The only structural difference: agent packages have
no `workflows[]` field.

A complete library example lives at
[`workflows/agents/code-reviewer-v1-agent-package.json`](../../workflows/agents/code-reviewer-v1-agent-package.json).

## Lifecycle

```
┌─────────────┐   export    ┌───────────────────┐   import     ┌─────────────┐
│ library row │ ──────────► │ .cf-agent-package │ ───────────► │ library row │
│  (key, v)   │             │      .json        │              │  (key, v')  │
└─────────────┘             └───────────────────┘              └─────────────┘
                                    ▲
                            assistant-authored
                                    │
                            ┌──────────────┐
                            │ chat draft   │
                            └──────────────┘
```

### Export

`GET /api/agents/{key}/{version}/package` — read-only, returns the
canonical document with a `Content-Disposition: attachment;
filename="<key>-v<version>-agent-package.json"` header. The agents
list page exposes a per-row Export button on each card; the download
streams through `HttpClient` so the auth interceptor attaches the
bearer token (anchor-style downloads bypass the interceptor and 401
in production).

The exporter walks the entry-point agent's role assignments and
recursively collects every referenced role + skill + MCP server at
its current state — the on-disk file is **fully self-contained**.

### Import

Two arrival paths land on the same imports surface:

1. **File upload** on `/workflows`. The imports page parses the JSON,
   reads `schemaVersion`, and dispatches: workflow-schema files route
   through `WorkflowsApi.previewPackageImport`; agent-schema files
   route through `AgentsApi.previewPackageImport`. The per-row
   resolution dropdown (`UseExisting` / `Bump` / `Copy` / `NewKey`),
   drift gate, and conflict-resolution modal are all schema-agnostic —
   one component handles both.
2. **Chat-side handoff** from a `save_agent_package` chip. The
   assistant's `save_agent_package` tool returns `preview_ok` /
   `preview_conflicts`; the chat panel renders an inline confirmation
   chip whose click writes a session-storage handoff (containing the
   package bytes, draft snapshot id, or artifact event id) and
   navigates to `/workflows`. The imports page reads the handoff on
   init and runs the same preview pipeline as a file upload.

The importer's preview returns a list of `WorkflowPackageImportItem`
rows — one per agent / role / skill / MCP server / role-assignment —
each carrying an `action` of `Create` / `Reuse` / `Conflict` /
`Refused`. The user resolves Conflicts row-by-row; the apply pass
hits `POST /api/agents/package/apply` which delegates to
`AgentPackageImporter` (a thin adapter that synthesizes a
`WorkflowPackage` with empty `Workflows[]` and reuses
`WorkflowPackageImporter`'s plumbing under the
`AgentPackageImportValidator` admission policy).

The imports page also surfaces the existing **drift gate**: if a
versioned entity's library max version moves between preview and
apply, the apply 409s with a structured payload of moved entries; the
user can Apply Anyway with `acknowledgeDrift: true` to commit against
the new max versions.

### Conflict resolutions

Same three shapes as workflow packages:

- **`UseExisting`** — drop the package's row; rewrite every reference
  to the entity to point at the library's existing version.
- **`Bump`** — set the entity's version to `existingMaxVersion + 1`.
  Valid only for versioned kinds (Agent). The new agent row carries
  `forkedFromKey` / `forkedFromVersion` lineage.
- **`Copy`** — rename the entity to a new `key` at version `1`. Valid
  only for versioned kinds; the user supplies the new key.

`UseExisting` on the entry-point agent is invalid (the entry-point
reference would no longer resolve); the apply path rejects it with a
clear error.

### Assistant authoring

The homepage assistant has five tools dedicated to agent-package
authoring (mirror of the workflow set):

| Tool | When |
|---|---|
| `set_agent_package_draft({ package })` | First emission. Writes `draft.cf-agent-package.json` to the conversation workspace, records an `AgentPackageDraft` artifact event. |
| `patch_agent_package_draft({ operations[] })` | Refinement. Applies an RFC 6902 JSON Patch in place; cheap edit path so the LLM doesn't re-emit the full payload on every tweak. |
| `get_agent_package_draft()` | Read the current draft state — usually to compute a patch path. |
| `clear_agent_package_draft()` | User-initiated only. Refuses while a Save chip has a pending snapshot. |
| `save_agent_package()` | Validate + offer the user the Save confirmation chip. Reads from the on-disk draft so the LLM doesn't have to round-trip the package. |

The `agent-authoring` skill (loaded on demand via
`load_assistant_skill({ key: "agent-authoring" })`) carries the
curriculum — drafting flow, save-result branches, conflict-resolution
modes, redaction recovery, shape gotchas, and a complete reference
exemplar. **Tool-precedence-first**: the skill steers the model to
call the recording tools rather than emit a fenced JSON block in
chat.

### Artifacts and rail recovery

Every `set_agent_package_draft` / `patch_agent_package_draft` /
`save_agent_package` invocation records an artifact event:

- `AgentPackageDraft` (kind `5`) — the live draft. Re-set + re-patch
  supersede the prior live draft event.
- `AgentPackageSnapshot` (kind `6`) — immutable per-save snapshot
  bound to a Save chip. Apply marks the row expired.

Both kinds render in the chat panel's artifact rail with the same
Download / View / Diff / Save-from-rail buttons as workflow kinds.
**Rail recovery** is the path for "I closed the chip — where did my
draft go?": the rail's Save button on a snapshot row pre-loads the
imports page with the snapshot's bytes, equivalent to clicking the
chip directly.

## Operator guide

### Importing the example

```sh
# From the repo root, upload the library example through the imports page
# (requires AgentsRead + AgentsWrite + AgentRolesWrite + SkillsWrite + McpServersWrite).
$ open http://localhost:5174/workflows
# Drag workflows/agents/code-reviewer-v1-agent-package.json onto the
# "Import JSON" drop zone. The preview lists 5 rows:
#   Agent  code-reviewer            v1   Create
#   Role   code-reviewer-tools           Create
#   Skill  redact-pii                    Create
#   McpServer codeflow-docs              Create
#   AgentRoleAssignment code-reviewer    Create
# Click Apply (no resolutions needed on a clean DB).
```

### Exporting an agent

```sh
# From the agents page, click the per-row Export button on any agent
# card. The browser saves <key>-v<version>-agent-package.json.
# The exported file is fully self-contained — every role / skill / MCP
# server the agent transitively references is bundled at its current
# library state.
```

### Authoring a fresh agent via the assistant

```
User:  Create a code-reviewer agent. It should approve clean diffs and
       reject with concrete feedback. Give it read access to the working
       tree and a docs search MCP.

Asst:  [load_assistant_skill { key: "agent-authoring" }]
       [set_agent_package_draft { package: { schemaVersion: "codeflow.agent-package.v1", … } }]
       Saved a draft. The agent is wired to the codeflow-docs MCP for
       cross-referencing API contracts. Want me to add a redact-pii
       skill for the critique output?

User:  Yes.

Asst:  [patch_agent_package_draft { operations: [
         { op: "add", path: "/skills/-", value: { name: "redact-pii", body: "…" } },
         { op: "add", path: "/roles/0/skillNames/-", value: "redact-pii" }
       ]}]
       [save_agent_package]
       Preview validated — `code-reviewer v1` + 1 role + 1 skill + 1
       MCP server, all marked Create. Click Save to add to the library.
```

The `Save` chip routes to `/workflows`, which detects the agent schema
and runs the same preview / apply pipeline. The user lands on the
imports page with a one-row Apply button.

### Editing a seeded library agent

```
User:  Edit the prompt template of `dev/coder` to mention the new
       guardrails.

Asst:  [load_assistant_skill { key: "agent-authoring" }]
       [get_agent { key: "dev/coder" }]
       [set_agent_package_draft { package: { schemaVersion: "codeflow.agent-package.v1",
         entryPoint: { key: "dev/coder", version: 1 }, … } }]
       [patch_agent_package_draft { operations: [
         { op: "replace", path: "/agents/0/config/promptTemplate", value: "…" }
       ]}]
       [save_agent_package]
       Preview returned a Conflict on `dev/coder` v1 — the library
       already has v1 with a different template. The chip below offers
       to resolve in the imports page where you can pick Bump
       (creates v2 with your edit) or Copy (forks under a new key).
```

## Failure modes

| Symptom | Likely cause | Remedy |
|---|---|---|
| `Agent package export failed self-containment check. Missing N reference(s)` on `GET .../{key}/{version}/package` | Library row references a role / skill / MCP server that's been deleted. | Either restore the missing entity or drop the role assignment + try export again. |
| `package-schema-unsupported` on import | Schema string doesn't match `codeflow.agent-package.v1`. | Check the file's `schemaVersion` field. |
| `package-entry-point-missing` on import | The `entryPoint.key` + `entryPoint.version` pair isn't in `agents[]`. | Verify the entry-point matches an embedded agent row. |
| `Conflict` row on Apply that mentions an MCP server | The library has an existing MCP server at the same key with a different endpoint URL or tool list. | Pick `UseExisting` if the live row is correct, `Copy` to import under a new key. |
| Save chip never renders after `save_agent_package` returns `preview_ok` | Draft path needs a `snapshotId`; verify the tool result includes it. | Re-emit the draft via `patch_agent_package_draft`, then call `save_agent_package` again. |

## See also

- [Workflow authoring](../authoring-workflows.md) — the parent surface
  for workflows that compose multiple agents.
- [Assistant artifacts](../assistant-artifacts.md) — the rail / pill
  contract that powers `AgentPackageDraft` / `AgentPackageSnapshot`
  recovery.
