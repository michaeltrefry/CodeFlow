# Code-aware workflows

This is the canonical reference for the Code-Aware Workflows feature: per-trace working
directories, the `repositories` input convention, the framework-managed `workflow.traceWorkDir` /
`workflow.traceId` variables, the `setup_workspace` host tool, the `vcs.open_pr` /
`vcs.get_repo` host tools, and the cleanup model that ties it all together. If you're authoring
a workflow that needs to clone, edit, commit, push, or open a PR, start here.

The bootstrap step — clone, base-branch resolution, feature-branch creation, first push to
register credentials — is collapsed into a single atomic, idempotent host tool
(`setup_workspace`). Authors don't choreograph any of that from agent prompts.

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

`setup_workspace` and the `vcs.*` tools require a configured Git host (`GitHostSettings.Mode`
+ token). Until those are configured `setup_workspace` returns the structured error
`auth_unavailable` and `vcs.*` returns `error: not_configured`.

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
| `setup_workspace` returns `auth_unavailable` | No `GitHostSettings` token is configured for any of the requested repository hosts. Configure the host or remove the repo from the call. The error fires at task 1 (instead of after hours of LLM work) because `setup_workspace` exercises the credential boundary on its first push. |
| `setup_workspace` returns `host_not_allowed` | URL host isn't on `GitHostSettings.AllowedHosts`. Add the host to the allowlist or use a URL pointing at the configured host. |
| Agent's `git push` fails with "Authentication failed" mid-workflow | Repo wasn't seen by `setup_workspace` (so the cred-file entry is missing). Call `setup_workspace([{url}])` for the new repo; the merge is idempotent for already-cloned repos. |
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

### The setup-agent pattern: one tool call

The setup agent's job is one tool invocation. Given the trace's `workflow.repositories` input,
call:

```
setup_workspace({
  repositories: [{ url: "https://github.com/foo/bar.git" }, …]
})
```

The tool returns:

```json
{
  "repos": [{
    "url": "https://github.com/foo/bar.git",
    "localPath": "bar",
    "baseBranch": "main",
    "featureBranch": "task/3b70fc02",
    "baseSha": "abc123…",
    "alreadyPresent": false
  }]
}
```

`setup_workspace` automatically stages `setWorkflow("repositories", result.repositories)` on
submit, so downstream agents read `workflow.repositories[i].featureBranch` / `localPath` /
`baseBranch` directly — no parsing, no remote calls, no manual mirror by the agent. The
framework's per-trace allowlist (`saga.RepositoriesJson`) keys off the same array so the
rich author-facing shape and the slim runtime contract stay in lockstep.

What the tool does for you per repo, atomically:

- **Resolves the upstream default branch** via `git ls-remote --symref origin HEAD`. The
  workflow targets `master` / `develop` / `trunk` correctly without the author having to know.
- **Clones** into `{traceWorkDir}/<repo-basename>`. Path-confined so escapes fail with
  `path_confined`.
- **Creates the feature branch** as `<featureBranchPrefix>/<traceId-short>` off the resolved
  base.
- **Pushes once with `-u`**, which exercises the credential helper at task 1 instead of after
  hours of LLM work. Any auth failure surfaces immediately as `auth_unavailable` /
  `push_failed` rather than stranding development on a local branch.
- **Captures the base SHA** so reviewers can diff against the exact starting point.
- **Stages a `setWorkflow("repositories", […])` update** so subsequent `vcs.open_pr` calls
  pass the per-trace allowlist check.

#### Idempotent merge / mid-flow addition

Re-calling `setup_workspace` is the supported way to add a repo discovered mid-workflow (the
architect or coding agent realises the dev work touches a missing dependency). Existing repos
round-trip with `alreadyPresent: true` — no re-clone, no re-push — and the new one goes
through the full pipeline. The tool re-stages `setWorkflow("repositories", …)` with the
merged array on every call, so downstream agents and `saga.RepositoriesJson` always reflect
the latest set without the agent doing anything manual.

#### Structured errors

`setup_workspace` returns `IsError: true` results with a stable `code` for every failure shape.
Agents handle these by surfacing the code (HITL, abort port, message back to operator) — never
by retrying or pattern-matching against free-text.

| code | meaning |
| --- | --- |
| `auth_unavailable` | No token configured for the requested host. |
| `host_not_allowed` | URL host isn't on `GitHostSettings.AllowedHosts`. |
| `url_invalid` | URL didn't parse / wasn't an `https://…/.git` shape. |
| `path_confined` | Resolved local path escapes `traceWorkDir`. |
| `clone_failed` | `git clone` exited non-zero. |
| `branch_create_failed` | `git checkout -b <featureBranch>` failed. |
| `push_failed` | First push to register the feature branch failed. |
| `base_branch_lookup_failed` | `git ls-remote --symref origin HEAD` failed. |
| `base_branch_mismatch` | Caller-supplied `branch` disagrees with remote HEAD. |
| `rev_parse_failed` | Couldn't capture the base SHA. |
| `credential_file_write_failed` | Couldn't write the per-trace cred file. |
| `stage_repositories_failed` | Workflow-bag stage write rejected the value. |

The agent's role only needs `setup_workspace` to handle the bootstrap. Add `read_file`,
`apply_patch`, `run_command` for the dev work that follows; the seeded `code-worker` and
`code-builder` system roles already include all of these.

### Auth model: how `git` knows the token

Agents never see the configured Git host token. Authentication flows through a per-trace
credential helper that the platform writes once and every spawned `git` process picks up
automatically. The plumbing:

1. **`git` is on PATH** in the worker and api runtime images. `run_command "git", [...]`
   "just works" — no provider abstraction in the path.
2. **`setup_workspace` populates a per-trace credential file** at the time of its first
   invocation, in git's native credential-store wire format (one URL per host, e.g.
   `https://x-access-token:TOKEN@github.com`). The file lives at
   `WorkspaceOptions.GitCredentialRoot/{traceId-N}` (default `/var/lib/codeflow/git-creds`),
   mode `0600`, owned by the app uid. **It is outside `WorkingDirectoryRoot`**, so the agent's
   path-confined `read_file` and cwd-confined `run_command` cannot reach it. Calling
   `setup_workspace` again to add a repo (the idempotent-merge flow) updates the file in
   place if a new host appears.
3. **`git` is pointed at the file** via `GIT_CONFIG_*` env vars set on every spawned `git`
   process by both `run_command` and `IGitCli`. Specifically:
   `credential.helper = store --file=…/{traceId-N}` and `credential.useHttpPath = true`.
   No global gitconfig mutation, so concurrent traces in the same worker never collide.
4. **Cleanup** runs on the same paths that remove the per-trace workdir: trace-delete,
   happy-path completion, and the periodic `GitCredentialSweepService` (TTL = same
   `WorkingDirectoryMaxAgeDays` setting).

What this means for the agent:

- `setup_workspace` runs `git clone` with the **clean** URL the agent provided — no embedded
  token, nothing in `.git/config`, nothing in process argv.
- After the bootstrap, **every other git op is plain `git`**: `run_command "git", ["add",
  "."]`, `run_command "git", ["commit", "-m", "…"]`, `run_command "git", ["push", "origin",
  "<branch>"]`. Auth flows through the credential helper transparently.
- A push attempt against a repo `setup_workspace` never saw is missing from the cred file and
  surfaces "Authentication failed" from git. The fix is to call `setup_workspace([{url}])`
  for the new repo (the idempotent merge handles already-present clones); it's not the
  workflow's job to mint URLs by hand.
- Token never appears in `run_command` stdout, the workspace tree, `.git/config`, or any
  variable an agent or script can read.

This is the contract for `git`-related actions. The narrow `vcs.*` surface below covers the
verbs that *aren't* a thin wrapper around `git`: API-level operations (`open_pr`, `get_repo`).

### The `vcs.*` host tools

Agents that need to interact with the configured Git host (GitHub or GitLab) call dedicated
host tools. The platform manages auth — agents never see or pass the token. Bootstrap is
covered by `setup_workspace`; the surface below is the publish boundary.

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
`Internal` / `Unknown`). `setup_workspace` does its own base-branch resolution via
`git ls-remote --symref origin HEAD`, so most code-aware workflows don't need this — it stays
available for the rare case where an agent wants the visibility / clone URL fields without
running git.

#### `vcs.clone` (deprecated)

Superseded by `setup_workspace`, which handles clone + base-branch resolution + feature-branch
creation + first push atomically and idempotently. The tool is still registered for back-compat
with imported workflow packages, but new packages should use `setup_workspace` instead — the
deprecation surface in the role editor flags it on assignment. The seeded `code-worker` and
`code-builder` system roles no longer grant `vcs.clone`.

#### Granting the tools

The tools live in the `Host` category and are visible in the role editor. The reference
`code-worker` system role grants the bootstrap + dev-work set:

```json
{
  "key": "code-worker",
  "toolGrants": [
    {"category": "Host", "toolIdentifier": "read_file"},
    {"category": "Host", "toolIdentifier": "apply_patch"},
    {"category": "Host", "toolIdentifier": "run_command"},
    {"category": "Host", "toolIdentifier": "setup_workspace"},
    {"category": "Host", "toolIdentifier": "vcs.open_pr"},
    {"category": "Host", "toolIdentifier": "vcs.get_repo"},
    {"category": "Host", "toolIdentifier": "echo"},
    {"category": "Host", "toolIdentifier": "now"}
  ]
}
```

`setup_workspace` is the bootstrap entry point; `vcs.open_pr` is the publish boundary. The
two together cover both ends of a code-aware workflow.

### The PR-publishing pattern

Per-repo flow:

1. Push any commits made since the last `git push` from the repo's `localPath` via
   `run_command "git", ["push", "origin", "<featureBranch>"]`. The first push was already
   issued by `setup_workspace`, so this is a fast-forward (or a no-op if no new commits).
2. Parse `<owner>` and `<name>` from `workflow.repositories[i].url`.
3. Call `vcs.open_pr` with `{ owner, name, head: <featureBranch>, base: <baseBranch>, title,
   body }`. Don't compose REST calls or pass tokens. The `baseBranch` is whatever
   `setup_workspace` resolved — read it from `workflow.repositories[i].baseBranch`, don't re-resolve.
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

The minimum viable code-aware workflow has three nodes wired in sequence:

```
Start (setup) → Agent (developer / reviewer loop) → Agent (publish) → terminal
```

Recipe:

1. Declare two workflow inputs:
   - `input` (Text) — the implementation plan or task brief.
   - `repositories` (Json, required) — the repo list.
2. Setup agent: one `setup_workspace({repositories: workflow.repositories})` tool call.
   The tool stages `setWorkflow("repositories", result.repositories)` itself on submit, so
   the agent does nothing further. Assign the seeded `code-worker` role (already includes
   `setup_workspace`).
3. Dev work: any combination of single agents, ReviewLoop, or Subflows. Each reads
   `workflow.repositories[i].localPath` / `featureBranch` / `baseBranch` for paths and runs
   `read_file`, `apply_patch`, `run_command "git", [...]` for the actual changes. The
   credential helper handles auth on every git invocation.
4. Publish agent: parses `<owner>` / `<name>` from `workflow.repositories[i].url`, calls
   `vcs.open_pr({owner, name, head: featureBranch, base: baseBranch, …})`. Assign the
   `code-worker` role (already includes `vcs.open_pr`).

Things you do **not** need to do:

- ❌ Compute the trace's workdir path yourself. `setup_workspace` writes into
  `workflow.traceWorkDir` and reports the resolved `localPath` per repo.
- ❌ Compute the feature branch name in agent prose. `setup_workspace` derives it.
- ❌ Resolve the upstream default branch. `setup_workspace` runs `git ls-remote` for you.
- ❌ Push on first commit "to exercise the credential boundary." `setup_workspace` does that
  on its first invocation.
- ❌ Manage a token or compose REST calls for PR creation. Call `vcs.open_pr`.
- ❌ Schedule cleanup. The platform handles it.

For a discovered mid-workflow dependency, call `setup_workspace([{url: <new-url>}])` again
from whichever agent noticed; the merge is idempotent and existing repos report
`alreadyPresent: true`.

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
- `workflows/dev-flow-v1-package.json` and `workflows/dev-flow-v2-package.json` — pre-
  `setup_workspace` reference implementations. They predate the atomic-bootstrap design and
  use the legacy choreography (`vcs.clone` + `run_command git checkout -b` + manual base-
  branch lookup); a refresh against `setup_workspace` is tracked as follow-up authoring work.
