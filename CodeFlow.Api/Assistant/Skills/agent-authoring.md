---
key: agent-authoring
name: Agent authoring
description: Draft, refine, and save importable agent packages.
trigger: any user request to create / draft / save / import / package an individual agent (without touching a workflow).
---

# Agent authoring

This skill carries the curriculum the assistant needs to drive a focused,
multi-turn dialogue with the user and emit a complete, importable agent
package — exactly one agent plus its role / skill / MCP-server closure.
The package is a draft only; the user explicitly clicks Save (or imports
the JSON file via the imports page) to persist anything to the library.

## READ THIS FIRST: how to record an agent package

The canonical action is **calling the recording tools**, not emitting
fenced-block JSON in your message body. Authoring an agent package has
exactly one wire path: `set_agent_package_draft` (once, to seed the
draft), then `patch_agent_package_draft` (many times, for each refinement),
then `save_agent_package` (zero-arg, to validate + offer the user the
Save chip). Never write a `cf-agent-package` block in chat as the
"primary" output — the exemplar at the bottom of this skill is reference
material the model reads to mirror shape, not an emission contract.

If a user message asks you to "show the JSON" or "print the package,"
call `get_agent_package_draft` to read the draft from disk and let the
chat panel render the result. Do not paste the package into your message
body — the rail's downloadable pill already hands the user the bytes.

## When to load this skill

Load `agent-authoring` when the user asks to:

- create a new agent from scratch (clean-slate authoring),
- edit an existing seeded or library agent (pull it via `get_agent`,
  use it as a template, save the refined version under a new key),
- bump an existing agent's version with a tweaked system prompt /
  output ports / role grants,
- tighten the role + tool / skill grants attached to an agent,
- introduce a new MCP server an agent depends on, or
- refresh an agent's tags or budget.

Do NOT load this skill for **workflow-level** authoring. The
`workflow-authoring` skill covers the broader case (workflow nodes,
edges, ports, multi-agent orchestration). Loading both at once is
redundant when the user's goal is a single agent.

## What's in an agent package

An agent package is the minimal self-contained bundle to recreate one
agent in another workspace. Schema version is the literal string
`"codeflow.agent-package.v1"`. The shape mirrors the workflow package
minus the workflow rows:

- **`schemaVersion`** — `"codeflow.agent-package.v1"` (literal).
- **`metadata`** — `{ exportedFrom, exportedAtUtc }`.
- **`entryPoint`** — `{ key, version }` of the entry-point agent.
- **`agents`** — list of `WorkflowPackageAgent` objects. Today this is
  always exactly one agent (the entry point); the field is a list so
  future "agent preset" packages can carry a small dependency closure
  of secondary agents under a shared role / skill set.
- **`agentRoleAssignments`** — list of `{ agentKey, roleKeys[] }` rows.
  Typically one row mapping the entry-point agent to its assigned roles.
- **`roles`** — every role any included agent assignment references.
  Roles are unversioned (key-keyed); the package carries them at their
  current state.
- **`skills`** — every skill any included role grants. Skills are
  unversioned (name-keyed).
- **`mcpServers`** — every MCP server any included role's `Mcp` tool
  grants reference. Servers are unversioned (key-keyed); transport,
  endpoint URL, and tools are carried.
- **`manifest`** — optional flat summary; the editor's package preview
  reads this. Agent-package manifest carries `{ agent, roles[],
  skills[], mcpServers[] }` (singular `agent`, not a list).

## Authoring vocabulary

### Agents

The unit of work this skill produces. Each agent has a `key` (slug-shaped,
lowercase-dashed), a `version` (positive integer), a `kind` (`Agent` or
`Hitl`), a `config` blob (the typed agent configuration), and zero or
more output `outputs` rows. Agents are **immutable per version**: every
edit creates a new `version` row in the registry. The package's
`agentVersion` MUST be a concrete positive integer — version `0` and
`null` are both invalid.

Agent `config` carries the runtime fields the LLM-side care about:
provider, model, system prompt, prompt template, declared output ports,
optional `partialPins`, optional `budget`. Mirror the exemplar exactly
when in doubt; the importer round-trips the JSON verbatim, so a typo
in `config.provider` lands as an unauthored agent.

#### Built-in agent tools

Every agent has three platform-managed tools wired in regardless of role:

- `submit({ port, ... })` — terminates the turn on the chosen output port.
  The artifact handed downstream is the agent's **assistant message
  content**, not the submit payload. Write your full response as the
  message body BEFORE calling `submit`.
- `setWorkflow(key, value)` — writes a small structured value into the
  trace's `workflow` bag.
- `setContext(key, value)` — writes into the per-saga `context` bag.

These tools are not declared in the agent package; they're injected by
the runtime. Don't list them as toolGrants.

#### Output declarations

Every port the agent's `submit` calls reach must be declared in
`config.outputs[]` AND in the top-level `outputs[]` of the
`WorkflowPackageAgent`. The two lists must agree. A port the agent
routes through but doesn't declare is rejected at import-time on the
target library.

### Roles

Reusable named bundles of tool grants + skill grants. Each role has a
`key` (slug-shaped), `displayName`, `description`, `tags[]`, a
`toolGrants[]` list, and a `skillNames[]` list.

A `toolGrant` carries `{ category, toolIdentifier }`:

- `category: "Host"` — a built-in host tool. `toolIdentifier` is the
  tool's bare name (`read_file`, `list_directory`, `setup_workspace`).
- `category: "Mcp"` — an MCP server tool. `toolIdentifier` follows the
  exact form `mcp:<server_key>:<tool_name>` (e.g. `mcp:demo-docs:search`).
  The importer parses the middle segment to find the server in the
  package's `mcpServers[]`.
- `category: "Skill"` — a skill grant. Rare on roles; usually skills
  are listed via `skillNames[]` instead.

A role with `Mcp` grants implies the package MUST embed the matching
`mcpServers[]` entry. The exporter emits both halves; if you're hand-
authoring, mirror that.

### Skills

Reusable, unversioned text bodies (name-keyed) the assistant or the
agent runtime loads on demand. Each skill has a `name`, `body`,
`isArchived` flag, and timestamp metadata. Skills granted to a role
ride into the package via `roles[].skillNames[]` and the matching
`skills[]` row.

### MCP servers

External tool servers reachable over HTTP/SSE or stdio. Each server has
a `key` (slug), `displayName`, `transport` (`HttpSse` / `Stdio`),
`endpointUrl`, `tools[]` list with parameter schemas. Bearer tokens are
NEVER included in the package — `hasBearerToken: true` is a hint to the
target library that it must configure the token locally after import.

## Drafting flow

1. **Seed the draft once.** Call `set_agent_package_draft({ package: <full-shape> })`
   with the complete package object. The tool writes
   `draft.cf-agent-package.json` to the conversation workspace and
   returns a small summary; it does NOT echo the package back. The
   model never sees the full payload again from this point — that's
   the whole point of the draft path.

2. **Refine via JSON Patch.** For every subsequent edit, call
   `patch_agent_package_draft({ operations: [...] })` with RFC 6902
   ops. Examples:

   - Replace a system prompt:
     `{ "op": "replace", "path": "/agents/0/config/systemPrompt", "value": "..." }`
   - Add a tag:
     `{ "op": "add", "path": "/agents/0/tags/-", "value": "ops" }`
   - Add a role grant:
     `{ "op": "add", "path": "/roles/0/toolGrants/-", "value": { "category": "Host", "toolIdentifier": "list_directory" } }`
   - Bump the entry-point version:
     `{ "op": "replace", "path": "/entryPoint/version", "value": 2 }`
     (also update `/agents/0/version` to match).

   Use `/-` as the array index to append. The tool returns the updated
   summary on success or an error describing which operation failed.

3. **Inspect when needed.** Call `get_agent_package_draft()` to read
   the current draft state. Useful when computing the right path for
   a patch op, or when the user asks "what does the draft look like
   right now?" — the chat panel renders the result; do NOT paste the
   payload into your message body.

4. **Save when ready.** Call `save_agent_package()` (zero-arg form,
   reads from disk). The tool runs the package through the importer's
   preview path and snapshots the validated bytes to a per-save GUID
   file so the chip applies the EXACT bytes the user confirmed.

5. **Tear down only when the user asks.** Call
   `clear_agent_package_draft()` ONLY when the user explicitly says
   they're done with this draft and want to start a fresh design. The
   tool refuses while a Save chip is still pending the user's click —
   so a `preview_ok` followed by `clear_agent_package_draft` will
   bounce you off, which is intentional. Wait for the user.

## Save result branches

`save_agent_package` returns one of these shapes:

- **`status: "preview_ok"`** — preview validated. The chat panel
  renders a Save chip the user clicks to apply. Do NOT call the tool
  again or take further action; wait for the user's next message.
  The result carries `snapshotId` for the chip's apply-from-draft body.

- **`status: "preview_conflicts"`** — the package has Conflict or
  Refused rows that block apply. The chat panel renders a "Resolve
  in imports page" chip handing the package off to the imports page,
  where the user picks a per-row resolution (`UseExisting` / `Bump` /
  `Copy`). Default: surface the conflicts and wait. If the user
  explicitly asks you to fix a specific conflict (e.g. by editing the
  agent's system prompt to match the library), patch the draft and
  call `save_agent_package` again.

- **`status: "invalid"`** — admission or self-containment failed.
  Common causes: schema string typo, entry point not in `agents[]`,
  a role references an MCP server not embedded in the package. The
  result includes `missingReferences` so you can patch the draft to
  embed the missing entity.

- A bare `{ "error": "..." }` (no `status` field) — the tool itself
  failed before validation ran (workspace not writable, draft missing,
  agent admission validator misconfigured).

## Self-containment rule

The package MUST carry every entity any included agent transitively
references. The importer does NOT resolve a missing role / skill /
MCP server from the target library against the embedded `entryPoint` —
it expects the closure inside the package. **Exception:** the
importer DOES resolve refs to entities that already exist in the
target library at the same key/version — those land as `Reuse` rows
in the preview without needing to be embedded. Embed an entity only
when you're creating it or intentionally bumping its version.

The exporter (`GET /api/agents/{key}/{version}/package`) always emits
fully self-contained bundles for portability; the import-side
relaxation is an authoring affordance.

## Conflict resolution

When the importer detects a key collision (e.g. you're saving an agent
key that already exists at a higher version, or a role key that
already exists with different grants), it emits a `Conflict` row in
the preview. The user resolves each row through the imports page chip:

- **`UseExisting`** — drop the package's version of this entity; rewrite
  every reference to point at the library's existing version. Common
  when an agent's tooling matches the library's already-published role.
- **`Bump`** — set the entity's version to `existingMaxVersion + 1`.
  Valid only for versioned kinds (agents). The new agent row carries
  `forkedFromKey` / `forkedFromVersion` lineage.
- **`Copy`** — rename the entity to a new `key` at version `1`. Valid
  only for versioned kinds; the user supplies the new key.

If the user explicitly asks you to resolve a specific conflict
(e.g. "edit the role body to match the library version"), patch the
draft to align the package with the library's existing entity, then
call `save_agent_package` again. The default still applies — wait
for the user before re-emitting on conflicts.

## Redaction recovery

If your `package` argument arrives as
`{ "_redacted": true, "sha256": ..., "summary": ... }`, that is the
runtime's transcript stub for a payload you previously emitted — NOT
a callable input. The recovery procedure:

1. Call `get_agent_package_draft()` to surface the current bytes.
2. Compute the targeted JSON Patch ops the user's request needs.
3. Call `patch_agent_package_draft({ operations: [...] })`.

Do NOT re-emit the full package via `set_agent_package_draft` for an
edit — that is the expensive path you just bounced off. Re-emitting
is correct only when starting a fresh design, not when iterating on
an existing draft.

## Editing flow for a seeded library agent

A common case: the user wants to fork a seeded agent (e.g. `dev/coder`)
into a new variant. The flow:

1. Call `get_agent({ key: "dev/coder" })` to pull the canonical config.
2. Call `set_agent_package_draft({ package: <copy with new key> })` —
   the package contains the agent at version `1` under the new key,
   plus every role / skill / MCP server the original references at
   their CURRENT library state.
3. Patch the draft to apply the user's requested deltas (system prompt
   changes, additional role grants, bumped budget).
4. Call `save_agent_package` and let the user click the chip.

## Shape gotchas

- The schema field is `schemaVersion`, not `$schema` or `schema`. Its
  value is the literal string `"codeflow.agent-package.v1"`.
- The entry point's `(key, version)` MUST match exactly one row in
  `agents[]`. The admission validator rejects with code
  `package-entry-point-missing` otherwise.
- `agentVersion` is a positive integer. `0`, `null`, and missing all
  fail admission.
- Output ports the agent routes through MUST appear in BOTH
  `agents[].config.outputs[]` AND `agents[].outputs[]` (the
  `WorkflowPackageAgent` shape has its own outputs list distinct from
  the embedded config). Keep them in sync.
- MCP tool identifiers MUST follow the form
  `mcp:<server_key>:<tool_name>`. The importer splits on `:` and
  parses three segments — anything else (e.g. `mcp/server/tool`) is a
  hard rejection.
- `manifest.agent` is singular, not a list. The exporter emits a
  single `WorkflowPackageReference`; mirror it.
- Bearer tokens are NEVER in the package. If an MCP server has
  `hasBearerToken: true`, the user re-configures the token after
  import.

## Canonical shape exemplar

The block below is a complete, importable agent package showing every
DTO. **Mirror this shape exactly when computing patches** — field
names, enum casing, nesting. The exemplar uses placeholder dates and
keys; your draft should pick fresh ones aligned with the user's domain.

This is **reference only**. The primary action is still
`set_agent_package_draft` — do not paste this block into your message
body as the answer to a "create an agent" request.

```cf-agent-package
{
  "schemaVersion": "codeflow.agent-package.v1",
  "metadata": {
    "exportedFrom": "assistant-draft",
    "exportedAtUtc": "2026-05-09T00:00:00Z"
  },
  "entryPoint": { "key": "demo-writer", "version": 1 },
  "agents": [
    {
      "key": "demo-writer",
      "version": 1,
      "kind": "Agent",
      "config": {
        "type": "agent",
        "name": "Demo Writer",
        "provider": "openai",
        "model": "gpt-5.4",
        "systemPrompt": "Draft the requested artifact. Write the full draft as your message body BEFORE calling submit. Then submit on `Done`.",
        "promptTemplate": "## Brief\n{{ input }}",
        "maxTokens": 4000,
        "temperature": 0.4,
        "budget": {
          "maxToolCalls": 32,
          "maxLoopDuration": "00:10:00"
        },
        "outputs": [
          { "kind": "Done", "description": "Draft emitted." }
        ]
      },
      "createdAtUtc": "2026-05-09T00:00:00Z",
      "createdBy": "assistant-draft",
      "tags": ["demo", "author"],
      "outputs": [
        { "kind": "Done", "description": "Draft emitted." }
      ]
    }
  ],
  "agentRoleAssignments": [
    { "agentKey": "demo-writer", "roleKeys": ["demo-writer-tools"] }
  ],
  "roles": [
    {
      "key": "demo-writer-tools",
      "displayName": "Demo Writer Tools",
      "description": "Read access for the writer so it can pull reference docs + a redaction skill.",
      "isArchived": false,
      "tags": ["demo", "author"],
      "toolGrants": [
        { "category": "Host", "toolIdentifier": "read_file" },
        { "category": "Mcp", "toolIdentifier": "mcp:demo-docs:search" }
      ],
      "skillNames": ["redact-pii"]
    }
  ],
  "skills": [
    {
      "name": "redact-pii",
      "body": "Redact personally identifiable information before emitting any draft.",
      "isArchived": false,
      "createdAtUtc": "2026-05-09T00:00:00Z",
      "createdBy": "assistant-draft",
      "updatedAtUtc": "2026-05-09T00:00:00Z",
      "updatedBy": "assistant-draft"
    }
  ],
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
          "syncedAtUtc": "2026-05-09T00:00:00Z"
        }
      ]
    }
  ],
  "manifest": {
    "agent": { "key": "demo-writer", "version": 1 },
    "roles": ["demo-writer-tools"],
    "skills": ["redact-pii"],
    "mcpServers": ["demo-docs"]
  }
}
```
