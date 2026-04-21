# Security Model — Workspace & VCS Tools (v1)

This document describes the security posture of CodeFlow's source-control
integration (`workspace.*` and `vcs.*` tools). It is intentionally explicit
about what is **not** mitigated in v1 so operators can decide where to enable
each tool.

## TL;DR

- **`workspace.exec` is high-risk**: it runs agent-directed commands on the
  CodeFlow host with no sandbox boundary. Only grant it in roles on instances
  that serve **trusted** authors. Do not enable it on a multi-tenant instance
  until process sandboxing lands.
- The read-only workspace tools (`workspace.open`, `workspace.list_files`,
  `workspace.read_file`) and the `vcs.*` family are safer: each is confined
  to the workspace root or mediated by the VCS provider, and tokens are
  never written to disk.

## Architecture boundaries

| Boundary | What enforces it |
| --- | --- |
| Workspace root containment for file ops | `PathConfinement.Resolve` (resolves symlinks and rejects paths that escape the worktree). |
| Default-branch protection on push | `VcsToolProvider.PushAsync` compares `workspace.CurrentBranch` to `workspace.DefaultBranch` before invoking `git push`. |
| Force-push not available | `IGitCli` does not expose any force flag; `PushAsync` / `PushWithBearerAsync` never pass `--force` or `--force-with-lease`. |
| Token never written to disk | `GitCli.PushWithBearerAsync` injects the token via `-c http.extraheader=AUTHORIZATION: bearer <token>` for a single process invocation only. Error paths redact the token via `RunAsync(..., redactToken: ...)`. |
| Token not retained in memory | `IGitHostTokenProvider.AcquireAsync` returns a disposable `GitHostTokenLease`; disposal clears the captured string. |
| Repo-host allowlist | `RepoUrlHostGuard` rejects URLs whose host doesn't match the configured `GitHostSettings.Mode`. |
| Correlation isolation | `WorkspaceService` scopes each worktree under `{root}/work/{correlationId}/{repoSlug}`; branches are `codeflow/{correlation[:8]}/...` so two workflows on the same repo never share a working tree. |

## `workspace.exec` threat model

### What `workspace.exec` does

Runs `Process.Start(command, args[])` with:
- `UseShellExecute = false`
- `WorkingDirectory = workspace.RootPath`
- `ArgumentList` populated from the tool's literal arg list (no shell parsing)
- `Environment` cleared and repopulated only from `WorkspaceOptions.ExecEnvAllowlist`
- Per-call timeout with process-tree kill
- Output capped and tail-truncated above `ExecOutputMaxBytes`

### What is mitigated

- **Shell injection**: arg-array invocation means `; rm -rf /` passed as a
  filename is a filename, not a shell command. Tested in
  `WorkspaceExecToolTests.Exec_arg_array_treats_shell_metacharacters_as_literal`.
- **Ambient environment leakage**: non-allowlisted env vars do not cross
  into the child process. Tested in
  `WorkspaceExecToolTests.Exec_env_allowlist_omits_vars_not_in_allowlist`.
- **Runaway processes**: the per-call timeout kills the entire process tree
  and returns partial output with `timedOut=true`.
- **Output exhaustion**: stdout/stderr each capped independently; older
  bytes drop first with a `[... truncated N leading bytes ...]` marker.

### What is **not** mitigated in v1

- **Arbitrary binary execution**: the agent can invoke anything it can find
  on `PATH` (git, python, npm, system compilers, …). Nothing prevents it
  from running `curl`, `ssh`, or a package manager.
- **Outbound network**: no egress filtering. An agent that can run `curl`
  or `pip install` can fetch anything reachable from the host.
- **Writes outside the workspace via exec**: `PathConfinement` only
  protects the file tools. An executed program can write anywhere the
  CodeFlow process user can write.
- **Disk exhaustion**: no quota on the workspace root beyond what the OS
  provides.
- **CPU / memory quotas**: none.
- **Resource cleanup after detach**: background processes that detach
  (`nohup &`) are outside the `Kill(entireProcessTree: true)` boundary and
  will outlive the tool call.
- **Secrets reachable by the process user**: if the CodeFlow host has
  `~/.aws/credentials`, `~/.ssh/id_rsa`, or similar dotfiles readable by
  the process user, an agent can read them via exec (or even via
  `workspace.read_file` if the path resolves inside the workspace, which
  `PathConfinement` prevents — but exec can read any file the process
  user can).

### Operator guidance

- **Single-operator instances**: enabling `workspace.exec` is reasonable
  when the agent authors are the same humans who run the host.
- **Multi-tenant / untrusted-author instances**: **do not** grant
  `workspace.exec` in any role until we add a sandbox boundary (container
  per call, seccomp profile, or similar).
- **CI systems**: if agent-generated commits go through a CI pipeline,
  treat that pipeline as the real enforcement point; assume anything the
  agent produced is attacker-controlled.

### UI surface

The role editor tags `workspace.exec` with `IsMutating: true` and the
tool description calls out the risk explicitly ("HIGH RISK — grants the
agent arbitrary code execution on the host"). The role editor also shows
an explicit warning banner when a role includes `workspace.exec` in its
grants.

## Non-goals (v1)

- Process sandboxing / containment
- Egress network policy
- GPG or SSH signing of commits
- Git LFS (detected and rejected with a clear error if encountered)
- Multi-account support on a single host (one GitHub **or** one GitLab
  token per instance, enforced by `RepoUrlHostGuard`)
- Multi-tenant hardening of the runtime-level workspace state

## Reporting

Security issues in CodeFlow should be reported privately to the
repository owner before any public disclosure.
