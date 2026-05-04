# Code-aware workflows

This is the canonical reference for the Code-Aware Workflows feature: per-trace working
directories, the `repositories` input convention, the framework-managed `workflow.traceWorkDir` /
`workflow.traceId` variables, the `vcs.*` host tools, and the cleanup model that ties it all
together. If you're authoring a workflow that needs to clone, edit, commit, push, or open a PR,
start here.

## Operator setup

### Configure the working directory root

The platform writes per-trace workdirs under `WorkspaceOptions.WorkingDirectoryRoot` — a
deployment-level constant, not an admin-UI setting. The default is `/workspace`, which both
api and worker containers mount as a shared host volume (`codeflow-workdir` /
`${CODEFLOW_WORKDIRS_DIR}`). Override via the `Workspace__WorkingDirectoryRoot` environment
variable when running outside the standard container layout (e.g. integration tests pointing at
a per-test temp dir).

The host path under that volume must be writable by the CodeFlow service account (UID 1654, the
`APP_UID` baked into the api/worker images — see `deploy/.env.example`). The init container in
the prod compose stack chowns it on every `up`.

The `vcs.*` tools (including `vcs.clone`) require a configured Git host (`GitHostSettings.Mode` +
token). Until those are configured the tools return `error: not_configured`.

### Configure the git-credential root

Per-trace git credential files live at `WorkspaceOptions.GitCredentialRoot/{traceId-N}`
(default `/var/lib/codeflow/git-creds`). The Dockerfile pre-creates this directory at mode
`0700` owned by the app uid. Override via `Workspace__GitCredentialRoot` for non-container dev;
the override path must be writable by `APP_UID` and **must not** sit inside `WorkingDirectoryRoot`
or anywhere else an agent can reach via `read_file` / `run_command`. The contents are short-lived
plain-text tokens — exposing them defeats the credential-helper boundary.

### Sweep TTL

The periodic per-trace workdir sweep runs once per hour from any host running `AddCodeFlowHost`
(typically the worker). The TTL is driven by `GitHostSettings.WorkingDirectoryMaxAgeDays`
(default 14 days); a value of `null`, `0`, or any non-positive number falls back to the default.

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
   `workflow.repositories` has a non-empty `prUrl`, the workdir is deleted.
3. **Periodic sweep** — catches anything the first two missed past the configured TTL.

For a manual cleanup, just `rm -rf {root}/{traceId-N}/`. The platform tolerates entries
disappearing out from under it.

### Troubleshooting

| Symptom | Likely cause |
|---|---|
| Trace launch fails with "Failed to create per-trace working directory" | `WorkingDirectoryRoot` set but path doesn't exist or isn't writable. The save-time validator should have caught this; check whether the dir was deleted post-save. |
| Code-setup agent's `run_command` fails with "no active workspace context" | The trace-launch endpoint didn't seed `workflow.traceWorkDir`. Confirm `Workspace__WorkingDirectoryRoot` is set and the directory is mounted on both api and worker. |
| `vcs.open_pr` returns `error: not_configured` | `GitHostSettings` has no token, or GitLab mode is configured without a base URL. |
| Stale workdirs accumulating | Sweep service might not be running. Confirm `AddCodeFlowHost` is wired into the host (look for "Workdir sweep" logs). |
| Agent's `git push` fails with "Authentication failed" | Either no `GitHostSettings` token configured, or the repo's host doesn't match the configured one (e.g. pushing to gitlab.com when only github.com is set). Check the trace's `repositories` input — every host the workflow will push to must map to a configured token. |
| `git` not found on PATH | The runtime image isn't recent enough. The git binary was added in epic 658 — rebuild the worker / api image. |

---

## Authoring guide

Skip ahead to [Authoring a code-aware workflow](#authoring-a-code-aware-workflow) for the
recipe; the sections below introduce each primitive.

### Framework-managed workflow variables

The platform seeds read-only entries in the per-trace-tree variable bag at trace launch:

| Key | Value | Set by |
|---|---|---|
| `traceWorkDir` | `{WorkingDirectoryRoot}/{traceId.ToString("N")}` — the per-trace workspace path | `TracesEndpoints.CreateTraceAsync` |
| `traceId` | `traceId.ToString("N")` — 32-char hex, no hyphens | `TracesEndpoints.CreateTraceAsync` |

Both are listed in `ProtectedVariables.ReservedKeys`. Scripts and agents cannot overwrite them:

- `setWorkflow('traceWorkDir', ...)` from a Logic-node script fails the evaluation with
  `LogicNodeFailureKind.ReservedWorkflowKeyWrite`.
- The `setWorkflow` agent tool returns an error tool result; the bag is unchanged.

The runtime source of truth is `WorkflowSagaStateEntity.TraceWorkDir` (a typed saga field
introduced in epic sc-593) — `workflow.traceWorkDir` is the script-author-facing alias.
Tool-execution context is built from the saga field directly, not from the bag entry.

The error message in both cases names the offending key and explains "framework-managed
workflow variable." If you need framework-seeded value X, propose adding it to the registry —
don't try to masquerade as it from author code.

Subflow / ReviewLoop children **inherit a snapshot** of these values at fork time. So a child
saga's `workflow.traceId` and `workflow.traceWorkDir` are the *parent's* values — which is what
you want for stable branch naming and a shared workspace across the entire workflow tree.

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

1. Read `workflow.repositories` and `workflow.traceWorkDir`.
2. For each repo: call `vcs.clone({url, path: <repo-name>, branch: <base-branch>})` to
   materialize it under `{traceWorkDir}/<repo-name>` (auth is platform-managed; the agent never sees
   a token), then `git checkout -b <feature-branch>` via `run_command`. The feature-branch name
   is **pre-computed** in the prompt template via
   `{{ branch_name workflow.prdTitle workflow.traceId }}` — the agent's instructions explicitly
   say "do not invent or modify the slug." Agents that discover repos mid-flight call `vcs.clone`
   the same way; `repos[]` declared up-front is just a hint for context-engineering, not a
   precondition.
3. Mid-turn, `setWorkflow('repositories', <merged-array-with-localPath-and-featureBranch>)` so
   downstream agents (and any subflows) see:
   ```json
   [{
     "url": "...",
     "branch": "main",
     "localPath": "/workspace/{traceId-N}/<repo>",
     "featureBranch": "add-todo-list-3b70fc02"
   }]
   ```

The agent's role grants `read_file`, `apply_patch`, `run_command` (for `git checkout`, `commit`,
`push`), `vcs.clone` (for the materialization), and the optional `vcs.get_repo` if the agent
needs to discover the upstream default branch before the clone call.

### Auth model: how `git` knows the token

Agents never see the configured Git host token, but they can still use plain `git` for everything
once a repo is cloned. The platform achieves this by:

1. **Installing `git` on PATH** in the worker and api runtime images. `run_command "git", [...]`
   "just works" — no provider abstraction in the path.
2. **Writing a per-trace credential file** at trace start, in git's native credential-store wire
   format (one URL per host, e.g. `https://x-access-token:TOKEN@github.com`). The file lives at
   `WorkspaceOptions.GitCredentialRoot/{traceId-N}` (default `/var/lib/codeflow/git-creds`),
   mode `0600`, owned by the app uid. **It is outside `WorkingDirectoryRoot`**, so the agent's
   path-confined `read_file` and cwd-confined `run_command` cannot reach it.
3. **Pointing `git` at the file** via `GIT_CONFIG_*` env vars set on every spawned `git` process
   by both `run_command` and `IGitCli`. Specifically:
   `credential.helper = store --file=…/{traceId-N}` and `credential.useHttpPath = true`.
   No global gitconfig mutation, so concurrent traces in the same worker never collide.
4. **Cleaning up** on the same path that removes the per-trace workdir: trace-delete,
   happy-path completion, and the periodic `GitCredentialSweepService` (TTL = same
   `WorkingDirectoryMaxAgeDays` setting).

What this means for the agent:

- `vcs.clone` runs `git clone` with the **clean** URL the agent provided — no embedded token,
  nothing in `.git/config`, nothing in process argv.
- After the clone, **everything else is plain `git`**: `run_command "git", ["add", "."]`,
  `run_command "git", ["commit", "-m", "…"]`, `run_command "git", ["push", "origin", "<branch>"]`.
  Auth flows through the credential helper transparently.
- Pushing to a repo whose host is **not** the configured Git host produces a clear
  `Authentication failed` error from git — the helper has no entry for that host. Declare
  every repo the workflow will touch in the `repositories` input.
- Token never appears in `run_command` stdout, the workspace tree, `.git/config`, or any
  variable an agent or script can read.

This is the contract for `git`-related actions. The narrow `vcs.*` surface below covers the
verbs that *aren't* a thin wrapper around `git`: API-level operations (`open_pr`, `get_repo`)
and the clone entry point that registers the workspace.

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

#### `vcs.clone`

Materializes a repo into the active workspace. Auth flows through the per-trace credential
helper described in [Auth model](#auth-model-how-git-knows-the-token); the agent passes the
clean URL and the platform handles authentication invisibly. Inputs:

- `url` (string) — repo URL on the configured host. Validated against the host guard so
  cross-host cloning is denied.
- `path` (string, optional) — workspace-relative destination. Must stay confined to the trace's
  workdir. Defaults to the repo's basename (e.g. `myrepo` for `https://github.com/foo/myrepo`).
- `branch` (string, optional) — branch or tag to check out. Defaults to the repo's default.
- `depth` (integer, optional) — `--depth` for a shallow clone. Omit for a full clone.

Returns on success: `{ path, branch, head, defaultBranch }` — `head` is the resolved commit SHA.

Refuses if the destination already exists (with anything in it), so calling `vcs.clone` twice for
the same destination is a tool error rather than a silent overwrite. Use `run_command` (`git
fetch`/`git checkout`) for follow-up navigation on an already-cloned repo.

#### Granting the tools

The tools live in the `Host` category and are visible in the role editor. The reference
`code-worker` role in `workflows/dev-flow-v1-package.json` grants the read+write set:

```json
{
  "key": "code-worker",
  "toolGrants": [
    {"category": "Host", "toolIdentifier": "read_file"},
    {"category": "Host", "toolIdentifier": "apply_patch"},
    {"category": "Host", "toolIdentifier": "run_command"},
    {"category": "Host", "toolIdentifier": "vcs.clone"},
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
4. Mid-turn, `setWorkflow('repositories', ...)` to update each entry with the returned `prUrl`.
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
   in `workflow.repositories` has a non-empty `prUrl`, the workdir is deleted. Read by the
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
3. Add your dev work between setup and publish. The dev agent reads `workflow.repositories[i].localPath`
   to know where each clone lives, edits files, runs tests, commits.
4. Reuse or copy the `publish` agent. Same role.
5. Optional: an `inputScript` on the `Start` node can extract the PRD title and stash it as
   `workflow.prdTitle` so the setup agent's `branch_name` filter has a meaningful slug source. See
   the existing inputScript in `dev-flow-v1-package.json`'s start node for the regex.

Things you do **not** need to do:

- ❌ Compute the trace's workdir path yourself. Read `workflow.traceWorkDir`.
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
- `docs/subflows.md` — child sagas, including how workflow variables (and therefore `traceWorkDir`/`traceId`)
  are inherited via copy-on-fork.
- `workflows/dev-flow-v1-package.json` — the reference implementation; every concept in this
  doc has a corresponding piece in that package.
