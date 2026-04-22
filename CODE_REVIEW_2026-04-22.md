# Full Codebase Review — CodeFlow — 2026-04-22

## Executive summary
- Repository at a glance: .NET backend plus Angular UI, 272 production source files reviewed, about 22.5k LOC across backend and UI.
- Coverage: reviewed production code in `CodeFlow.Api`, `CodeFlow.Host`, `CodeFlow.Worker`, `CodeFlow.Orchestration`, `CodeFlow.Runtime`, `CodeFlow.Persistence`, and `codeflow-ui/src`. Excluded test projects, build artifacts, `node_modules`, and EF Core migrations except for spot checks on persistence behavior.
- Headline: the highest-risk issues cluster around trust boundaries and state management. Authorization can fail open when the external permissions service is degraded, agent execution is exposed behind a read-only permission, MCP and workspace integration paths are overly trusting, and several caches or subscriber paths can become permanently unhealthy or unbounded under failure.
- Counts: Critical: 0 | High: 10 | Medium: 3 | Low: 1 | Info: 0.

## Critical findings
None.

## High findings
#### [F-001] Deny access when the company permissions backend is degraded
- **Category:** security
- **Severity:** High
- **Location:** `CodeFlow.Api/Auth/CompanyPermissionChecker.cs:46-63`
- **Finding:** In company-auth mode, the checker falls back to role-based permissions when the permissions API returns an empty set. Because `PermissionsApiClient` also returns an empty set on request failures and non-success responses, outages and backend errors silently widen access instead of failing closed.
- **Impact:** Any authenticated user carrying a broad role claim can receive permissions that the authoritative company permissions service would otherwise deny. This is exactly the kind of trust-boundary failure that shows up during partial outages.
- **Suggested fix:** Only use role-based permissions when the company permissions backend is explicitly disabled. Treat fetch failures and empty responses from an enabled backend as deny-by-default, and surface the degraded state operationally.
- **Confidence:** High

#### [F-002] Require a write-level permission for live agent execution
- **Category:** security
- **Severity:** High
- **Location:** `CodeFlow.Api/Endpoints/AgentTestEndpoints.cs:20-21`, `CodeFlow.Api/Endpoints/AgentTestEndpoints.cs:82-109`
- **Finding:** The `/api/agent-test` endpoint is guarded by `AgentsRead` even though it performs a real invocation, resolves tool grants, and can execute external model and tool calls.
- **Impact:** A user who should only be able to view agent definitions can still trigger cost-incurring or side-effecting executions through the test surface.
- **Suggested fix:** Protect this endpoint with `AgentsWrite` or a dedicated execution permission, and review the UI to ensure only authorized operators can reach the test flow.
- **Confidence:** High

#### [F-003] Restrict MCP server endpoints to approved destinations
- **Category:** security
- **Severity:** High
- **Location:** `CodeFlow.Api/Endpoints/McpServersEndpoints.cs:259-307`
- **Finding:** MCP server create and update validation accepts any absolute URI. That allows a caller with `McpServersWrite` to direct server-side verification and tool discovery traffic at arbitrary internal or external endpoints.
- **Impact:** The application can be used as an SSRF primitive against internal services, metadata endpoints, or other network targets reachable from the backend.
- **Suggested fix:** Enforce an allowlist or explicit egress policy for MCP hosts, and at minimum restrict accepted schemes to approved `http`/`https` endpoints before persisting configuration.
- **Confidence:** High

#### [F-004] Evict failed MCP sessions instead of caching them forever
- **Category:** bad-pattern
- **Severity:** High
- **Location:** `CodeFlow.Runtime/Mcp/DefaultMcpClient.cs:38-46`
- **Finding:** `DefaultMcpClient` caches a `Lazy<Task<IMcpSession>>` per server key and never removes a faulted entry. One transient handshake, auth, or connectivity failure permanently poisons that server key until the process restarts.
- **Impact:** MCP-backed tools can remain unusable long after the underlying server recovers, producing a sticky outage that operators cannot heal without recycling the app.
- **Suggested fix:** Remove the cached entry when session creation fails or when the cached task faults, then allow the next invocation to attempt a fresh connection.
- **Confidence:** High

#### [F-005] Bound host-side script logs as part of the sandbox budget
- **Category:** security
- **Severity:** High
- **Location:** `CodeFlow.Orchestration/Scripting/LogicNodeScriptHost.cs:65-86`
- **Finding:** The Jint engine is memory-limited, but `log(message)` appends unbounded strings into a host-managed `List<string>` that sits outside Jint's accounting.
- **Impact:** A malicious or buggy logic script can still exhaust process memory and take down the worker despite the advertised 4 MB sandbox limit.
- **Suggested fix:** Cap total log bytes or entry count, truncate oversized messages, and fail evaluation once the host-side log budget is exceeded.
- **Confidence:** High

#### [F-006] Reject stale or out-of-order completion events by round
- **Category:** bad-pattern
- **Severity:** High
- **Location:** `CodeFlow.Orchestration/WorkflowSagaStateMachine.cs:41-44`, `CodeFlow.Orchestration/WorkflowSagaStateMachine.cs:122-132`
- **Finding:** Completion events are correlated only by `TraceId`, and the saga records the decision against `saga.CurrentRoundId` without checking that `message.RoundId` matches the active round.
- **Impact:** A delayed duplicate or stale completion from an earlier round can mutate the live saga state, append history to the wrong round, and route the workflow incorrectly.
- **Suggested fix:** Validate that the incoming completion's `RoundId` matches the saga's active round before applying it, or include round identity in correlation and rejection logic.
- **Confidence:** High

#### [F-007] Preserve full repository paths when deriving workspace identity
- **Category:** security
- **Severity:** High
- **Location:** `CodeFlow.Runtime/Workspace/RepoReference.cs:31-45`, `CodeFlow.Runtime/Workspace/WorkspaceService.cs:36-55`
- **Finding:** HTTP(S) repo parsing only preserves the first two path segments, so nested GitLab-style paths such as `group/subgroup/repo.git` collapse to the same `Owner` and `Name` shape as unrelated repositories. `WorkspaceService` then uses the resulting `Slug` for worktree identity and path layout.
- **Impact:** Distinct repositories can alias to the same mirror or worktree path, causing cross-repo contamination and the possibility of operating on the wrong checkout.
- **Suggested fix:** Preserve the full repository path in the parsed identity and derive a stable escaped slug or hash from the entire path, not just the first two segments.
- **Confidence:** High

#### [F-008] Deduplicate overlapping MCP grants before building the resolved tool list
- **Category:** bad-pattern
- **Severity:** High
- **Location:** `CodeFlow.Persistence/RoleResolutionService.cs:92-146`
- **Finding:** The role resolver deduplicates allowed tool names but does not deduplicate `McpToolDefinition` entries. If multiple assigned roles grant the same `mcp:<server>:<tool>`, `McpToolProvider` later materializes a dictionary keyed by the full tool name and throws.
- **Impact:** Legitimate multi-role assignments can crash tool resolution and block agent invocation entirely.
- **Suggested fix:** Deduplicate resolved MCP tool definitions by full name before returning them, or make the provider tolerant of duplicates.
- **Confidence:** High

#### [F-009] Preserve existing Git host tokens during non-secret edits
- **Category:** bad-pattern
- **Severity:** High
- **Location:** `codeflow-ui/src/app/pages/settings/git-host/git-host-settings.component.ts:160-183`, `CodeFlow.Persistence/GitHostSettingsRepository.cs:32-75`
- **Finding:** The Git host settings page allows Save when a token already exists, but it still submits `token: this.tokenValue()`. Because the API never returns the stored token and the repository requires a non-empty token on every save, any attempt to edit mode or base URL without replacing the token fails.
- **Impact:** Existing Git host settings cannot be updated safely in place, which breaks a core admin path and encourages unnecessary secret re-entry.
- **Suggested fix:** Add preserve/replace semantics for tokens on the API contract, or require explicit token replacement before enabling edits that depend on a secret update.
- **Confidence:** High

#### [F-010] Remove runtime fallback to predictable development database credentials
- **Category:** security
- **Severity:** High
- **Location:** `CodeFlow.Persistence/CodeFlowPersistenceDefaults.cs:3-13`, `CodeFlow.Host/HostExtensions.cs:77-81`
- **Finding:** If `CODEFLOW_DB_CONNECTION_STRING` is absent, the runtime host silently falls back to a hard-coded local MariaDB connection string with predictable host, database, username, and password values.
- **Impact:** A missing or mis-scoped production secret can connect the process to the wrong database using a known credential instead of failing fast, which is both a security and safety problem.
- **Suggested fix:** Remove the runtime fallback and require an explicit connection string outside design-time/dev-only entry points.
- **Confidence:** High

## Medium findings
#### [F-011] Make company-permission evaluation asynchronous end-to-end
- **Category:** efficiency
- **Severity:** Medium
- **Location:** `CodeFlow.Api/Auth/CompanyPermissionChecker.cs:68-88`
- **Finding:** `GetOrFetchPermissions` issues an outbound HTTP request and then blocks on it with `.GetAwaiter().GetResult()` inside the authorization path.
- **Impact:** Slow or stuck permissions calls tie up request threads, ignore request cancellation, and reduce throughput during auth-heavy traffic or upstream slowness.
- **Suggested fix:** Move permission retrieval onto an async path with an explicit timeout and caching strategy, or prefetch it before the authorization handler runs.
- **Confidence:** High

#### [F-012] Use bounded, trace-scoped queues for trace streaming
- **Category:** efficiency
- **Severity:** Medium
- **Location:** `CodeFlow.Api/TraceEvents/TraceEventBroker.cs:9-26`, `CodeFlow.Api/TraceEvents/TraceEventBroker.cs:33-40`
- **Finding:** The broker creates an unbounded channel per subscriber and publishes every event to every subscriber, even when most subscribers only care about a single trace.
- **Impact:** Slow clients can accumulate unbounded in-memory backlogs, and publish cost grows linearly with total active subscribers rather than subscribers for the relevant trace.
- **Suggested fix:** Partition subscriptions by `TraceId`, use bounded channels or backpressure, and drop or close slow subscribers instead of buffering indefinitely.
- **Confidence:** High

#### [F-013] Bound or expire repository version caches
- **Category:** efficiency
- **Severity:** Medium
- **Location:** `CodeFlow.Persistence/WorkflowRepository.cs:7-10`, `CodeFlow.Persistence/WorkflowRepository.cs:34-35`, `CodeFlow.Persistence/WorkflowRepository.cs:164-165`, `CodeFlow.Persistence/AgentConfigRepository.cs:8-10`, `CodeFlow.Persistence/AgentConfigRepository.cs:36-37`, `CodeFlow.Persistence/AgentConfigRepository.cs:84-85`
- **Finding:** Both repositories keep process-wide static `ConcurrentDictionary` caches for versioned objects and never evict old entries.
- **Impact:** Long-lived processes with ongoing workflow or agent churn will retain every distinct version they ever touch, creating permanent memory growth.
- **Suggested fix:** Replace the static dictionaries with a bounded cache that supports expiration or size limits, or remove the cache where the hit rate does not justify indefinite retention.
- **Confidence:** High

## Low findings
#### [F-014] Either validate declared ports or remove them from the script-validation contract
- **Category:** dead-code
- **Severity:** Low
- **Location:** `CodeFlow.Api/Endpoints/WorkflowsEndpoints.cs:45-64`
- **Finding:** `ValidateScriptRequest.DeclaredPorts` is accepted by the API but never read. The endpoint only checks syntax, even though the caller supplies the port set it expects to validate against.
- **Impact:** The validation API can report success for scripts that still fail immediately at runtime because they emit undeclared ports.
- **Suggested fix:** Thread declared ports into validation or remove the unused field from the request contract so the endpoint's behavior is honest.
- **Confidence:** High

## Informational
None.

## Themes and systemic observations
Several integration boundaries fail open or remain overly permissive. The permissions backend, agent test surface, MCP endpoint configuration, and workspace repo identity each trust external inputs more than they should for a control-plane system.

The codebase also has a recurring pattern of sticky or unbounded state. MCP session caching, trace streaming, and version caches all retain failure or growth state indefinitely instead of recovering or shedding load.

Admin configuration flows need tighter lifecycle semantics. Secret-preserving updates, verification freshness, and external dependency health are not modeled consistently, which makes operational status easier to misread than it should be.

## Coverage notes
Reviewed production code only. Test projects were excluded per request, and no fixes were made during this pass.

Build outputs, package caches, and generated artifacts were excluded. EF Core migrations were not reviewed line by line because they are generated history rather than active runtime logic.

This review was primarily static. I did not run the full integration stack, exercise live RabbitMQ/MariaDB/MCP services, or execute end-to-end UI flows, so findings tied to dynamic behavior should still be validated during remediation.
