# Code Review Findings - 2026-04-28

Full repository review findings for CodeFlow.

## Finding 1: [P1] Symlinked ancestors can escape workspace confinement

**File:** `CodeFlow.Runtime/Workspace/PathConfinement.cs:16-26`

PathConfinement only resolves the final candidate when it exists, so paths under a symlinked directory can still pass the lexical root check. Workspace tools that read, write, patch, or run commands can follow that parent symlink and operate outside the workspace.

**Recommendation:** Add tests for symlink descendants and resolve or reject every symlinked path component before use.

## Finding 2: [P1] Read-only shell role grants mutating commands

**File:** `CodeFlow.Persistence/SystemAgentRoles.cs:43-53`

The `read-only-shell` system role grants `run_command` even though that tool is marked mutating and executes arbitrary commands in the workspace. A role advertised as never mutating can delete files, rewrite repos, or perform other side effects.

**Recommendation:** Remove `run_command` from this role or replace it with an allowlisted read-only command surface.

## Finding 3: [P1] Workflow package apply bypasses resource-specific permissions

**File:** `CodeFlow.Api/Endpoints/WorkflowsEndpoints.cs:48-52`

The package preview/apply endpoints require only `WorkflowsWrite`, but applying a package can create skills, MCP servers, roles, role grants, agents, role assignments, and workflows. If these permissions are separated for a tenant, this endpoint collapses the model.

**Recommendation:** Require all affected write policies or reject package sections the caller cannot administer.

## Finding 4: [P1] Imported MCP servers bypass endpoint policy validation

**File:** `CodeFlow.Api/WorkflowPackages/WorkflowPackageImporter.cs:564-607`

Package import persists MCP server `EndpointUrl` values directly, while the normal MCP server create and update endpoints validate scheme, host, internal targets, and allowlists through `IMcpEndpointPolicy`. A package can therefore introduce an endpoint that normal APIs would reject.

**Recommendation:** Run the same endpoint policy during preview and apply before saving imported servers.

## Finding 5: [P2] VCS tools are not scoped to trace repositories

**File:** `CodeFlow.Runtime/Workspace/VcsHostToolService.cs:21-39`

`open_pr` accepts arbitrary owner and repository names and sends them to the configured VCS provider. An agent with this tool can target any repository the platform token can access, not just repositories declared in the trace or cloned into the workspace.

**Recommendation:** Bind VCS operations to the trace repository list or an explicit allowlist.

## Finding 6: [P2] CI exercises only a tiny auth test slice

**File:** `.github/workflows/ci.yml:25-31`

The CI workflow builds the solution but intentionally skips the broader test suites and runs only `AuthServiceCollectionExtensionsTests`. Runtime, orchestration, persistence, package import, and workspace confinement regressions can merge without automated coverage.

**Recommendation:** Split fast unit tests into CI and quarantine only known failing integration tests.

## Finding 7: [P3] Deploy docs reference stale workdir mount path

**File:** `deploy/.env.example:41-47`

The env example says the host directory is mounted as `/app/workdirs`, but docker-compose and `WorkspaceOptions` use `/app/codeflow/workdir`. Operators following this guidance can configure code-aware workflows against the wrong path.

**Recommendation:** Align the examples and docs with the actual container mount.

