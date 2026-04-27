# Code-aware workflows

This is the canonical reference for the Code-Aware Workflows feature: per-trace working
directories, the `repositories` input convention, the framework-managed `workflow.workDir` /
`workflow.traceId` variables, the `vcs.*` host tools, and the cleanup model that ties it all
together. If you're authoring a workflow that needs to clone, edit, commit, push, or open a PR,
start here.

## Operator setup

### Configure the working directory root

The platform writes per-trace workdirs under `GitHostSettings.WorkingDirectoryRoot`. Until that
setting is configured, code-aware workflows degrade gracefully — the `vcs.*` tools return
`error: not_configured` and the workdir-related workflow variables are simply not seeded.

Set it via `PUT /api/admin/git-host`:

```json
{
  "mode": "GitHub",
  "baseUrl": null,
  "workingDirectoryRoot": "/app/workdirs",
  "workingDirectoryMaxAgeDays": 14,
  "token": { "action": "Replace", "value": "ghp_..." }
}
```

Validation runs at save time:

- The path must be **absolute** (`Path.IsPathFullyQualified`).
- It is auto-created if missing — the API process calls `Directory.CreateDirectory` so operators
  don't need shell access to mkdir before saving. The path must therefore be one the api/worker
  process can write to: in the production stack that means a path under the host-bound
  `/app/workdirs` volume (operator chowns the host directory to UID 1654, the APP_UID baked into
  the api/worker images — see `deploy/.env.example`).
- It must be **writable** by the CodeFlow service account; the endpoint probes by writing +
  deleting a temp file.

Any failure surfaces as a `400 Bad Request` with a field-level error on `workingDirectoryRoot`,
not a runtime 500 at trace launch.

### Sweep TTL

`workingDirectoryMaxAgeDays` controls the periodic-sweep TTL. Default is 14 days. The sweep
service runs once per hour from any host running `AddCodeFlowHost` (typically the worker), reads
settings on each tick (no restart needed for config changes), and deletes any entry under
`WorkingDirectoryRoot` whose mtime is older than the cutoff.

A value of `null`, `0`, or any non-positive number is treated as "use the default" of 14 days.

### What's on disk

```
{WorkingDirectoryRoot}/
  ├── {traceId-N}/             # per top-level trace, {traceId.ToString("N")}
  │   ├── repo-a/              # cloned by the code-setup agent
  │   ├── repo-b/
  │   └── ...
  └── ...
```

- One subdirectory per **top-level trace**. Subflow / ReviewLoop children share the parent's
  workdir — they don't get their own.
- The path is `Path.Combine(WorkingDirectoryRoot, traceId.ToString("N"))` — 32-char hex, no
  hyphens.

### Manual cleanup

Three cleanup paths run automatically:

1. **Trace delete** (`DELETE /api/traces/{id}` or bulk-delete) — removes the workdir alongside
   the DB rows.
2. **Happy-path completion** — when a saga reaches `Completed` AND every entry in
   `context.repositories` has a non-empty `prUrl`, the workdir is deleted.
3. **Periodic sweep** — catches anything the first two missed past the configured TTL.

For a manual cleanup, just `rm -rf {root}/{traceId-N}/`. The platform tolerates entries
disappearing out from under it.

### Troubleshooting

| Symptom | Likely cause |
|---|---|
| Trace launch fails with "Failed to create per-trace working directory" | `WorkingDirectoryRoot` set but path doesn't exist or isn't writable. The save-time validator should have caught this; check whether the dir was deleted post-save. |
| Code-setup agent's `run_command` fails with "no active workspace context" | The trace-launch endpoint didn't seed `workflow.workDir`. Confirm `WorkingDirectoryRoot` is set on `GitHostSettings`. |
| `vcs.open_pr` returns `error: not_configured` | `GitHostSettings` has no token, or GitLab mode is configured without a base URL. |
| Stale workdirs accumulating | Sweep service might not be running. Confirm `AddCodeFlowHost` is wired into the host (look for "Workdir sweep" logs). |

---

## Authoring guide

Skip ahead to [Authoring a code-aware workflow](#authoring-a-code-aware-workflow) for the
recipe; the sections below introduce each primitive.

### Framework-managed workflow variables

The platform seeds two read-only entries in the per-trace-tree variable bag at trace launch:

| Key | Value | Set by |
|---|---|---|
| `workDir` | `{WorkingDirectoryRoot}/{traceId.ToString("N")}` — present only when `WorkingDirectoryRoot` is configured | `TracesEndpoints.CreateTraceAsync` |
| `traceId` | `traceId.ToString("N")` — 32-char hex, no hyphens | `TracesEndpoints.CreateTraceAsync` |

Both are listed in `ProtectedVariables.ReservedKeys`. Scripts and agents cannot overwrite them:

- `setWorkflow('workDir', ...)` from a Logic-node script fails the evaluation with
  `LogicNodeFailureKind.ReservedWorkflowKeyWrite`.
- The `setWorkflow` agent tool returns an error tool result; the bag is unchanged.

The error message in both cases names the offending key and explains "framework-managed
workflow variable." If you need framework-seeded value X, propose adding it to the registry —
don't try to masquerade as it from author code.

Subflow / ReviewLoop children **inherit a snapshot** of these values at fork time. So a child
saga's `workflow.traceId` is the *parent's* traceId — which is what you want for stable branch
naming across the entire workflow tree.

### The `repositories` input convention

A code-aware workflow declares a `Json`-kind input named `repositories`:

```json
{
  "key": "repositories",
  "displayName": "Repositories",
  "kind": "Json",
  "required": true,
  "ordinal": 1
}
```

The trace caller supplies the value as an array of `{url, branch?}` objects:

```json
[
  { "url": "https://github.com/foo/bar.git", "branch": "main" },
  { "url": "https://github.com/foo/baz.git" }
]
```

Per-entry rules, enforced by the workflow validator at save time when an input is named
`repositories` AND `Kind == Json`:

- `url` must be a non-empty string.
- `branch` (optional) must be a string if present (or `null`).
- The root must be an array; the entry must be an object.

The convention is keyed off the literal name `repositories` plus `Kind == Json`. Inputs of
`Kind == Text` named `repositories` are not subject to the shape rule; other Json inputs (any
key other than `repositories`) keep their existing latitude.

### The `branch_name` Scriban filter

Every Scriban template (agent prompt, decision-output template, anywhere the renderer reaches)
can call `branch_name` to derive a stable feature-branch name from a PRD title and a trace id:

```scriban
{{ branch_name workflow.prdTitle workflow.traceId }}
```

Output shape: `<slug>-<8hex>`.

- Slug: lowercase ASCII, words joined by `-`, max 40 chars truncated at the last word boundary,
  trailing dashes trimmed. Combining marks are stripped (so `Café` → `cafe`). Anything that
  collapses to empty falls back to `branch`.
- TraceId prefix: first 8 hex chars (no hyphens), lowercased. Padded with `0`s if shorter,
  fallback `00000000` if missing.

Stable for a given `(title, traceId)` pair — multi-repo workflows produce identical branch
names across repos, and any downstream PR-publishing agent picks up the same name without
coordination.

### The setup-agent pattern: clone + branch + writeback

The reference implementation is the `code-setup` agent in `workflows/dev-flow-v1-package.json`.
Its job, in three steps:

1. Read `context.repositories` and `workflow.workDir`.
2. For each repo: `git clone <url>` into `{workDir}`, `git checkout <branch>` (or stay on
   default), `git checkout -b <feature-branch>`. The feature-branch name is **pre-computed** in
   the prompt template via `{{ branch_name workflow.prdTitle workflow.traceId }}` — the agent's
   instructions explicitly say "do not invent or modify the slug."
3. Mid-turn, `setContext('repositories', <merged-array-with-localPath-and-featureBranch>)` so
   downstream agents see:
   ```json
   [{
     "url": "...",
     "branch": "main",
     "localPath": "/app/workdirs/{traceId-N}/<repo>",
     "featureBranch": "add-todo-list-3b70fc02"
   }]
   ```

The agent's role grants `read_file`, `apply_patch`, `run_command` (for `git`), and the optional
`vcs.get_repo` if the agent needs to discover the upstream default branch.

### The `vcs.*` host tools

Agents that need to interact with the configured Git host (GitHub or GitLab) call dedicated
host tools. The platform manages auth — agents never see or pass the token.

#### `vcs.open_pr`

Opens a pull / merge request. Inputs:

- `owner` (string) — repo owner / GitLab namespace.
- `name` (string) — repo name.
- `head` (string) — head branch (your feature branch).
- `base` (string) — base branch (the upstream you're merging into).
- `title` (string) — PR title.
- `body` (string, optional) — PR body markdown. Defaults to empty.

Returns on success: `{ url, number }`. The `url` is GitHub's `html_url` or GitLab's `web_url`.

Failure modes are returned as **structured tool errors** (the result has `IsError: true`):

```json
{ "error": "repo_not_found",  "message": "..." }
{ "error": "unauthorized",    "message": "..." }
{ "error": "rate_limited",    "message": "..." }
{ "error": "conflict",        "message": "..." }
{ "error": "not_configured",  "message": "..." }
{ "error": "vcs_error",       "message": "..." }
```

Agents should branch on the `error` code, not the message. The `not_configured` error means
GitHostSettings hasn't been set up yet — useful diagnostic for fresh deployments.

#### `vcs.get_repo`

Reads basic repo metadata: `defaultBranch`, `cloneUrl`, `visibility` (`Public` / `Private` /
`Internal` / `Unknown`). Useful before opening a PR to confirm the upstream default branch
without needing `git ls-remote`.

#### Granting the tools

The tools live in the `Host` category and are visible in the role editor. The reference
`code-worker` role in `workflows/dev-flow-v1-package.json` grants both:

```json
{
  "key": "code-worker",
  "toolGrants": [
    {"category": "Host", "toolIdentifier": "read_file"},
    {"category": "Host", "toolIdentifier": "apply_patch"},
    {"category": "Host", "toolIdentifier": "run_command"},
    {"category": "Host", "toolIdentifier": "vcs.open_pr"},
    {"category": "Host", "toolIdentifier": "vcs.get_repo"},
    {"category": "Host", "toolIdentifier": "echo"},
    {"category": "Host", "toolIdentifier": "now"}
  ]
}
```

### The PR-publishing pattern

The reference implementation is the `publish` agent in `workflows/dev-flow-v1-package.json`.
Per-repo flow:

1. From the repo's `localPath`, run `git push -u origin <featureBranch>` via `run_command`.
2. Parse `<owner>` and `<name>` from the repo's `url`.
3. Call `vcs.open_pr` with `{ owner, name, head: <featureBranch>, base: <upstream-default>,
   title, body }`. Don't compose REST calls or pass tokens.
4. Mid-turn, `setContext('repositories', ...)` to update each entry with the returned `prUrl`.
   Keep the rest of the entry intact.

On any push or `vcs.open_pr` failure, fall through to the implicit `Failed` port. The completion
cleanup hook only deletes the workdir when the agent reaches `Published` AND every repo has a
populated `prUrl`.

### Cleanup lifecycle

The platform has three cleanup paths, in order of precedence:

1. **Trace-delete cleanup** (Slice D): `DELETE /api/traces/{id}` and bulk-delete remove the
   workdir for top-level traces alongside DB rows. Best-effort — log warnings, never fail the
   API call.
2. **Happy-path completion cleanup** (Slice E): when a saga reaches `Completed` AND every entry
   in `context.repositories` has a non-empty `prUrl`, the workdir is deleted. Read by the
   `WhenEnter(Completed, ...)` hook in `WorkflowSagaStateMachine`. Failed runs (any failure
   mode, including partial PR success or non-`Completed` terminal states) keep the workdir for
   forensics.
3. **Periodic sweep** (Slice F): hourly background job under any host running
   `AddCodeFlowHost`. Walks `{WorkingDirectoryRoot}/*` and deletes anything older than
   `WorkingDirectoryMaxAgeDays` (default 14). Last line of defense.

The implication for authors: **a "Failed" run leaves its workdir alone** so you can `cd` in and
debug. The sweep eventually reclaims it.

---

## Authoring a code-aware workflow

The minimum viable code-aware workflow has four nodes wired in sequence:

```
Start (code-setup) → Agent (developer) → Agent (publish) → terminal
```

Recipe:

1. Declare two workflow inputs:
   - `input` (Text) — the implementation plan or task brief.
   - `repositories` (Json, required) — the repo list.
2. Reuse or copy the `code-setup` agent. It needs the `code-worker` role (or any role that
   grants `read_file`, `apply_patch`, `run_command`, `vcs.get_repo`).
3. Add your dev work between setup and publish. The dev agent reads `context.repositories[i].localPath`
   to know where each clone lives, edits files, runs tests, commits.
4. Reuse or copy the `publish` agent. Same role.
5. Optional: an `inputScript` on the `Start` node can extract the PRD title and stash it as
   `workflow.prdTitle` so the setup agent's `branch_name` filter has a meaningful slug source. See
   the existing inputScript in `dev-flow-v1-package.json`'s start node for the regex.

Things you do **not** need to do:

- ❌ Compute the trace's workdir path yourself. Read `workflow.workDir`.
- ❌ Compute the feature branch name in agent prose. Use `{{ branch_name title traceId }}`.
- ❌ Manage a token or compose REST calls for PR creation. Call `vcs.open_pr`.
- ❌ Schedule cleanup. The platform handles it.

---

## Cross-references

- `docs/port-model.md` — port and decision model. Especially relevant for the implicit `Failed`
  port that the publish agent falls through on errors.
- `docs/authoring-workflows.md` — general workflow-authoring concepts (this doc assumes you've
  read it).
- `docs/prompt-templates.md` — Scriban renderer, the `branch_name` filter joins the family of
  built-in filters.
- `docs/subflows.md` — child sagas, including how workflow variables (and therefore `workDir`/`traceId`)
  are inherited via copy-on-fork.
- `workflows/dev-flow-v1-package.json` — the reference implementation; every concept in this
  doc has a corresponding piece in that package.
