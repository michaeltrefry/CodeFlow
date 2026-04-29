# Backend Code Review — CodeFlow — 2026-04-28

## Executive summary
- Repository at a glance: .NET 8 backend (Api / Orchestration / Runtime / Persistence / Host / Worker / Contracts) plus an Angular UI. Backend production code reviewed: ~700 `.cs` files across the seven backend projects.
- Coverage: production backend code only. Tests, EF migrations, the `bench/` and `design_handoff_*` folders, the `.claude/worktrees/` shadow tree, and the entire `codeflow-ui/` tree were excluded per the user's "backend only" scope.
- Lenses applied per request: **patterns and practices**, **redundant code**, **refactor opportunities**, **unfinished stubs**. Security was deliberately skipped — the 2026‑04‑22 and 2026‑04‑28 reviews already cover that surface and remediation is in flight.
- Headline: the backend is functionally healthy and has no production `NotImplementedException`s or dangling TODO blocks. The real cost is shape: two giants (`WorkflowSagaStateMachine` ~2.5k LOC, `DryRunExecutor` ~1.9k LOC) re-implement the same workflow semantics in parallel, three large minimal-API endpoint files mix HTTP wiring with business logic, and three LLM clients duplicate retry/error-mapping plumbing. One real stub remains: the Swarm `Coordinator` protocol is enum-defined, contract-supported, and runtime-refused.
- Counts: Critical: 0 | High: 9 | Medium: 11 | Low: 4 | Info: 1.

## Critical findings
None.

## High findings

#### [F-001] Decompose `WorkflowSagaStateMachine` — workflow-routing god class
- **Category:** bad-pattern
- **Severity:** High
- **Location:** [CodeFlow.Orchestration/WorkflowSagaStateMachine.cs](CodeFlow.Orchestration/WorkflowSagaStateMachine.cs) (2,511 LOC) + [WorkflowSagaStateMachine.Swarm.cs](CodeFlow.Orchestration/WorkflowSagaStateMachine.Swarm.cs:1) (520 LOC)
- **Finding:** A single saga class owns: event correlation, completion routing for agents/subflows/swarm contributors, decision-output templating, P3 rejection-history accumulation, P4 mirror-to-workflow-var, P5 port replacement, retry-context synthesis, output-script execution, logic-chain resolution, transform-script execution, swarm protocol selection, and workdir cleanup. Per‑feature partials (only `Swarm` exists today) are tucked alongside the main file but their dispatch hooks still reach into the main file's `RouteCompletionAsync` (e.g. swarm checks at WorkflowSagaStateMachine.cs:755, swarm state clearing at Swarm.cs:371).
- **Impact:** Every new node kind (Transform shipped 2026-04-27, ReviewLoop, Swarm, Coordinator coming) widens this surface. Onboarding cost is real and the file is the single largest correctness risk in the system.
- **Suggested fix:** Move per-node-kind logic behind an `IWorkflowNodeDispatcher` (one implementation per kind: Agent, Hitl, Subflow, ReviewLoop, Swarm, Transform). Let the saga keep correlation/state-transition concerns and delegate dispatch + completion-routing to the per-kind handler. Move workdir cleanup behind an event handler (`TraceTerminated → TryCleanupWorkdir`) instead of inline in the saga.
- **Confidence:** High

#### [F-002] DryRunExecutor re-implements live-saga semantics in a 702-line switch
- **Category:** bad-pattern / redundant
- **Severity:** High
- **Location:** [CodeFlow.Orchestration/DryRun/DryRunExecutor.cs:176-877](CodeFlow.Orchestration/DryRun/DryRunExecutor.cs:176)
- **Finding:** A single `switch (currentNode.Kind)` covering Start, Agent, Logic, Hitl, Subflow, ReviewLoop, Transform, Swarm, with case bodies of 50–200 LOC. The whole executor (~1.9k LOC) is a parallel implementation of the saga's routing, kept in sync by hand. Memory notes the executor was rewritten to v4 for "full saga parity" — that parity has to be hand‑maintained on every saga edit.
- **Impact:** Every behavioural change to the saga must be mirrored here or the dry-run/replay diverges silently from production. This is exactly the trap memory `feedback_workflow_globals_via_scripts.md` and the "v4 parity" rewrite were trying to escape.
- **Suggested fix:** Combine with F-001 — once node dispatch is polymorphic, `DryRunExecutor` becomes a thin host that swaps the dispatcher's "publish handoff" side-effect for an in-memory recorder. Until then, every behaviour-bearing helper used in both files (decision template, retry context, P4/P5 transforms) should be extracted to shared services (see F-007, F-008, F-009).
- **Confidence:** High

#### [F-003] Anthropic client bypasses the OpenAI-compatible base — retry/error logic duplicated
- **Category:** bad-pattern / redundant
- **Severity:** High
- **Location:** [CodeFlow.Runtime/Anthropic/AnthropicModelClient.cs:116-186](CodeFlow.Runtime/Anthropic/AnthropicModelClient.cs:116) vs [CodeFlow.Runtime/OpenAICompatible/OpenAiCompatibleResponsesModelClientBase.cs:93-160](CodeFlow.Runtime/OpenAICompatible/OpenAiCompatibleResponsesModelClientBase.cs:93)
- **Finding:** Both files define `SendWithRetryAsync`, `ShouldRetry(HttpStatusCode)`, and `GetRetryDelay(...)` with the same signatures and ~95% identical bodies (Anthropic adds 529; jitter/back-off are otherwise the same). Anthropic is structured as a sibling rather than an inheritor of the OpenAI-compatible base.
- **Impact:** A retry-policy bugfix or a new transient status code requires two edits and risks drift; new providers (Bedrock, Azure-OpenAI, Vertex) will copy the same skeleton.
- **Suggested fix:** Lift retry/back-off/cancellation/error-translation into an abstract `ChatModelClientBase` that both Anthropic and the OpenAI-compatible client inherit from. Concrete classes only own URL/headers/payload-shape.
- **Confidence:** High

#### [F-004] Endpoint files mix HTTP wiring with business logic in 140–340-line handlers
- **Category:** bad-pattern
- **Severity:** High
- **Location:** [CodeFlow.Api/Endpoints/TracesEndpoints.cs:202-340](CodeFlow.Api/Endpoints/TracesEndpoints.cs:202) (`CreateTraceAsync`); [TracesEndpoints.cs:706-791](CodeFlow.Api/Endpoints/TracesEndpoints.cs:706) (`SubmitHitlDecisionAsync`); [WorkflowsEndpoints.cs:428-550](CodeFlow.Api/Endpoints/WorkflowsEndpoints.cs:428) (`CreateAsync`)
- **Finding:** These minimal-API delegates do everything inline: validation, repository fan-out, pipeline runs, template rendering, payload assembly, event publishing, response shaping. There is no per-operation handler/use-case layer.
- **Impact:** Logic is unreachable from anywhere except an HTTP request — no reuse from the assistant tools, no in-process invocation, hard to unit-test without an HTTP host. The endpoint files have grown to 700–1,300 LOC partly because of this.
- **Suggested fix:** Extract one handler per non-trivial operation (`CreateTraceHandler`, `SubmitHitlDecisionHandler`, `CreateWorkflowHandler`, …) exposing `ExecuteAsync(request, ct)`. The endpoint becomes auth + binding + `await handler.ExecuteAsync(...)` + result-to-IResult mapping.
- **Confidence:** High

#### [F-005] Inconsistent error-response shapes across the API
- **Category:** bad-pattern
- **Severity:** High
- **Location:** Pervasive in [CodeFlow.Api/Endpoints/](CodeFlow.Api/Endpoints/) — 132 `Results.{BadRequest|ValidationProblem|Problem|NotFound}` calls vs. 38 anonymous `new { error = "…" }` payloads
- **Finding:** Three competing error shapes coexist: anonymous `{ error: string }` (e.g. [TracesEndpoints.cs:731](CodeFlow.Api/Endpoints/TracesEndpoints.cs:731), `:584`, `:598`), `Results.ValidationProblem` with a single conventional key like `"package"` ([WorkflowsEndpoints.cs:455-458](CodeFlow.Api/Endpoints/WorkflowsEndpoints.cs:455), `:214-217`), and `Results.Problem(...)`.
- **Impact:** Clients (UI + assistant + tests) can't share an error-parsing path; the UI's 8× duplicated `formatError()` (out of scope here, but a downstream symptom) exists because there is no canonical shape.
- **Suggested fix:** Pick `Results.ValidationProblem` (or `ProblemDetails`) as the single shape; introduce `Results.ApiError(string message, IDictionary<string,string[]>? errors = null)` and replace all anonymous `{ error }` returns. Add an integration test that asserts every 4xx response conforms.
- **Confidence:** High

#### [F-006] Static `MemoryCache` pattern duplicated across repositories
- **Category:** redundant
- **Severity:** High
- **Location:** [CodeFlow.Persistence/AgentConfigRepository.cs:8-27](CodeFlow.Persistence/AgentConfigRepository.cs:8); [CodeFlow.Persistence/WorkflowRepository.cs:8-20](CodeFlow.Persistence/WorkflowRepository.cs:8)
- **Finding:** Both repositories instantiate a `private static MemoryCache` with the same configuration shape (size limit, sliding expiration, options builder) and ship a hand-rolled `ClearCacheForTests()` escape hatch. The 2026-04-22 review (F-013) already flagged this as unbounded; it remains, and the duplication itself is the maintenance hazard.
- **Impact:** Cache-policy changes need two edits; static state leaks between tests are duplicated; new cached repositories will copy the pattern.
- **Suggested fix:** Extract a `VersionedEntityCache<TKey,TValue>` (constructor-injected, scoped, or a single static helper) and have both repositories take a dependency on it. Use `IMemoryCache` with explicit options instead of `new MemoryCache(...)` for testability.
- **Confidence:** High

#### [F-007] Decision-output template rendering duplicated saga vs DryRun
- **Category:** redundant
- **Severity:** High
- **Location:** [CodeFlow.Orchestration/WorkflowSagaStateMachine.cs:1371-1452](CodeFlow.Orchestration/WorkflowSagaStateMachine.cs:1371) vs [CodeFlow.Orchestration/DryRun/DryRunExecutor.cs:1400-1491](CodeFlow.Orchestration/DryRun/DryRunExecutor.cs:1400)
- **Finding:** `TryApplyDecisionOutputTemplateAsync` exists in both files (~80 LOC each) with the same Scriban-template resolution, agent-config lookup, and rendering pipeline. They diverge on side-effect (saga mutates the active artifact, DryRun records a synthetic event) but the resolution logic is identical.
- **Impact:** Any change to template syntax, fallback behaviour, or include-resolution must be made twice. This is the most likely silent-drift point between dry-run and production.
- **Suggested fix:** Extract `IDecisionTemplateRenderer` whose `RenderAsync(node, agentConfig, payload, ct)` returns a rendered string + a structured "skipped because…" reason. Both call sites adapt the result to their own side-effect.
- **Confidence:** High

#### [F-008] Retry-context synthesis duplicated saga vs DryRun
- **Category:** redundant
- **Severity:** High
- **Location:** [WorkflowSagaStateMachine.cs:2357-2380](CodeFlow.Orchestration/WorkflowSagaStateMachine.cs:2357) (`BuildRetryContextForHandoff`, `CountPriorFailedAttempts`) vs [DryRunExecutor.cs:1819-1859](CodeFlow.Orchestration/DryRun/DryRunExecutor.cs:1819) (`BuildRetryContextNode`, `BuildRetryContextMessage`)
- **Finding:** Saga builds a `Contracts.RetryContext`; DryRun builds a `JsonNode` for diagnostic output. The "what counts as a prior failed attempt" rule is restated in both forms.
- **Impact:** Drift here means the dry-run preview misrepresents the retry context the production agent will actually receive — the exact divergence the v4 rewrite was meant to prevent.
- **Suggested fix:** Extract `IRetryContextBuilder` returning a structured value object; have saga serialize it to `Contracts.RetryContext` and DryRun serialize it to `JsonNode`.
- **Confidence:** High

#### [F-009] P4 mirror + P5 port-replacement transforms duplicated saga vs DryRun
- **Category:** redundant
- **Severity:** High
- **Location:** [WorkflowSagaStateMachine.cs:1188-1243](CodeFlow.Orchestration/WorkflowSagaStateMachine.cs:1188) (`ApplyMirrorOutputToWorkflow`, `TryApplyPortReplacementAsync`) vs [DryRunExecutor.cs:234-347](CodeFlow.Orchestration/DryRun/DryRunExecutor.cs:234) (inline in the Agent case)
- **Finding:** P4 (mirror agent output to workflow var, per `MirrorOutputToWorkflowVar`) and P5 (replace output port if `OutputPortReplacements` matches) are implemented twice. The saga path is method-extracted; the DryRun path is inlined into the switch arm. Both encode the same precedence rule and normalisation.
- **Impact:** Per the port-model redesign that shipped 2026-04-25, P4/P5 are part of the canonical agent contract — drift here changes user-observable port semantics.
- **Suggested fix:** Extract `ApplyAgentOutputTransforms(artifact, node, workflowVars, …) → (transformedArtifact, updatedVars, finalPort)`. One call site each.
- **Confidence:** High

## Medium findings

#### [F-010] `RouteCompletionAsync` and `RouteSubflowCompletionAsync` follow the same shape but only one threads P3
- **Category:** redundant / bad-pattern
- **Severity:** Medium
- **Location:** [WorkflowSagaStateMachine.cs:481-699](CodeFlow.Orchestration/WorkflowSagaStateMachine.cs:481) (subflow path) and `:711-970` (agent path)
- **Finding:** Both methods walk the same shape — script update → decision append → unwired-port handling → resolve target through logic chain → dispatch — but only the subflow path calls `AccumulateRejectionHistoryAsync` (P3 rejection history). DryRun's `ExecuteReviewLoopAsync` ([DryRunExecutor.cs:961-991](CodeFlow.Orchestration/DryRun/DryRunExecutor.cs:961)) treats P3 as a per-round step that fires on every ReviewLoop round, not only on subflow completions.
- **Impact:** Two completion paths with overlapping responsibilities and one asymmetric feature is the kind of difference that's invisible in code review and only surfaces as a behavioural bug. Likely unifies cleanly once F-001 lands.
- **Suggested fix:** Promote `RouteCompletionAsync` to a single method that takes a "completion source" descriptor (Agent / Subflow / Swarm) and pushes per-source steps (P3, mirror, …) behind named policy hooks.
- **Confidence:** Medium — the asymmetry is real; verifying it's a bug vs. intentional needs a product owner.

#### [F-011] `InvocationLoop` is a 866-LOC monolith mixing tool dispatch, context updates, and budget checks
- **Category:** refactor
- **Severity:** Medium
- **Location:** [CodeFlow.Runtime/InvocationLoop.cs](CodeFlow.Runtime/InvocationLoop.cs) (866 LOC)
- **Finding:** The loop interleaves: tool-schema construction (`:99-129`, includes `BuildSubmitTool`), tool invocation + exception handling (`:518-546`), `setContext`/`setGlobal` validation + size-bounding (`:710-817`), termination decision parsing, budget enforcement, and history shaping.
- **Impact:** The most-invoked code path in the runtime is also the hardest to test in isolation. The `feedback_invocation_loop_empty_submit_protocol.md` memory entry captures one bug class that lives here; more are likely hiding.
- **Suggested fix:** Split into `ToolSchemaBuilder` (static schema), `ToolInvoker` (dispatch + exception → tool_output marshalling), `ContextUpdateHandler` (the setContext/setGlobal/setOutput/setInput family with shared size limits and validation), and a slim `InvocationLoop` that drives them.
- **Confidence:** High

#### [F-012] `WorkspaceHostToolService` couples patch parsing, FS I/O, and process exec
- **Category:** refactor
- **Severity:** Medium
- **Location:** [CodeFlow.Runtime/Workspace/WorkspaceHostToolService.cs](CodeFlow.Runtime/Workspace/WorkspaceHostToolService.cs) (685 LOC); patch parser at `:563-640`; bounded read at `:355-380`
- **Finding:** Public surface is four tools, but the implementation includes an internal V4A-style patch parser, file-bounded read logic, command execution, and newline-detection — all in one class.
- **Impact:** Patch-parsing bugs (this is non-trivial code) live next to FS plumbing they don't depend on. Hard to test the parser in isolation.
- **Suggested fix:** Extract `WorkspacePatchDocument` (parse + apply) into its own class; extract bounded file I/O into `BoundedFileReader`. Service stays as the tool-facing facade.
- **Confidence:** High

#### [F-013] `WorkflowValidator` and `WorkflowPackageImporter` overlap on structural validation
- **Category:** redundant
- **Severity:** Medium
- **Location:** [CodeFlow.Api/Validation/WorkflowValidator.cs:46-327](CodeFlow.Api/Validation/WorkflowValidator.cs:46) and [CodeFlow.Api/WorkflowPackages/WorkflowPackageImporter.cs:269-327](CodeFlow.Api/WorkflowPackages/WorkflowPackageImporter.cs:269)
- **Finding:** Both check node references, agent keys, edge integrity, and entry-point existence. The validator owns the comprehensive async path (920 LOC); the importer does a sync subset over the package payload before persistence.
- **Impact:** When a new validation rule lands (new node kind, new constraint), it has to be added in both places or the importer will silently accept invalid graphs that the live validator would reject.
- **Suggested fix:** Extract `WorkflowStructureValidator` with `ValidateGraph(graph) → IReadOnlyList<ValidationIssue>` (sync, no DB) and let the async validator wrap it with agent/workflow-resolution checks. The importer calls the sync core directly.
- **Confidence:** High

#### [F-014] Manual entity→DTO mapping repeated 21+ times across endpoints
- **Category:** redundant
- **Severity:** Medium
- **Location:** [TracesEndpoints.cs:1039-1073](CodeFlow.Api/Endpoints/TracesEndpoints.cs:1039) (`MapSummary`, `MapHitl` overloads), [WorkflowsEndpoints.cs:831-856](CodeFlow.Api/Endpoints/WorkflowsEndpoints.cs:831), [WorkflowFixturesEndpoints.cs:244-250](CodeFlow.Api/Endpoints/WorkflowFixturesEndpoints.cs:244), and others
- **Finding:** Each endpoint file owns its own `MapSummary` / `MapDetail` private statics, hand-coding constructor-style projections.
- **Impact:** When a DTO field is added or renamed, every map function must be touched. There is no "this DTO is supposed to round-trip" assertion (CodeFlow.Contracts.Tests covers serialization but not field parity with entities).
- **Suggested fix:** Move mapping into per-entity extension methods (`AgentConfig.ToSummaryDto()`, `Workflow.ToDetailDto()`) under a `Mapping/` folder, or adopt Mapperly (source-generator, no runtime cost). Keep mapping next to the DTO so adding a field is a one-file edit.
- **Confidence:** High

#### [F-015] Repository `NotFound` exceptions are thrown for expected-miss lookups and caught at the endpoint
- **Category:** bad-pattern
- **Severity:** Medium
- **Location:** Throw sites in [AgentConfigRepository.cs](CodeFlow.Persistence/AgentConfigRepository.cs), [WorkflowRepository.cs](CodeFlow.Persistence/WorkflowRepository.cs); catch sites at [TracesEndpoints.cs:810](CodeFlow.Api/Endpoints/TracesEndpoints.cs:810), [WorkflowsEndpoints.cs:239,385,411](CodeFlow.Api/Endpoints/WorkflowsEndpoints.cs:239), [WorkflowPackageImporter.cs:165-168](CodeFlow.Api/WorkflowPackages/WorkflowPackageImporter.cs:165)
- **Finding:** "Lookup by key+version that the caller is allowed to miss" is modelled as a thrown `…NotFoundException` that the caller catches.
- **Impact:** Exception-as-flow on the request hot path, both for cost and for stack-trace noise in logs.
- **Suggested fix:** Add `TryGetAsync(key, version, ct) → (bool, T?)` (or `T?`) overloads on the repositories and use them in the endpoints. Keep the exception-throwing version for callers that genuinely treat absence as exceptional.
- **Confidence:** High

#### [F-016] Workdir cleanup is bolted onto the saga via a "happy-path" branch
- **Category:** bad-pattern
- **Severity:** Medium
- **Location:** [WorkflowSagaStateMachine.cs:138-164](CodeFlow.Orchestration/WorkflowSagaStateMachine.cs:138) (`TryCleanupHappyPathWorkdirAsync` + `AllRepositoriesHavePrUrl`)
- **Finding:** A workspace-cleanup decision (driven by "every repo has a prUrl") sits inside the routing state machine.
- **Impact:** Workspace lifecycle policy is now coupled to saga semantics. Per-trace workdir was the whole point of the code-aware-workflows epic; the cleanup TTL/policy will keep evolving and shouldn't drag the saga with it.
- **Suggested fix:** Publish a `TraceTerminated` event from the saga and let a dedicated cleanup consumer evaluate policy and call `IWorkspaceService`. The saga keeps no opinion about repo URLs.
- **Confidence:** Medium

#### [F-017] `GitHubVcsProvider` and `GitLabVcsProvider` duplicate the provider skeleton
- **Category:** redundant
- **Severity:** Medium
- **Location:** [CodeFlow.Runtime/Workspace/GitHubVcsProvider.cs](CodeFlow.Runtime/Workspace/GitHubVcsProvider.cs) (~154 LOC) and [GitLabVcsProvider.cs](CodeFlow.Runtime/Workspace/GitLabVcsProvider.cs) (~184 LOC)
- **Finding:** Both implement `IVcsProvider` with the same surface (`GetRepoMetadataAsync`, `OpenPullRequestAsync`), the same error taxonomy (`VcsUnauthorized`/`VcsRepoNotFound`/`VcsConflict`), and the same activity tracing. Differences are confined to HTTP shape (Octokit vs. raw REST).
- **Impact:** Adding Gitea/Forgejo/Bitbucket means another full skeleton; error normalisation will drift.
- **Suggested fix:** `VcsProviderBase` owns argument validation, error normalisation, and tracing; concrete classes implement `Task<RepoMetadata> FetchMetadataCoreAsync(...)` and `Task<PullRequest> OpenPullRequestCoreAsync(...)`.
- **Confidence:** High

#### [F-018] `EmptyJsonElementDictionary` declared in two endpoint files
- **Category:** redundant
- **Severity:** Medium
- **Location:** [WorkflowsEndpoints.cs:150-153](CodeFlow.Api/Endpoints/WorkflowsEndpoints.cs:150) and [AgentsEndpoints.cs:225-226](CodeFlow.Api/Endpoints/AgentsEndpoints.cs:225)
- **Finding:** Same `private static readonly IReadOnlyDictionary<string, JsonElement>` (and a related `EmptyInputElement`) defined twice.
- **Impact:** Trivial maintenance burden — one of the cleanest "extract to constants" wins available.
- **Suggested fix:** Move both to `CodeFlow.Api/Endpoints/EndpointDefaults.cs` (or `CodeFlow.Contracts`).
- **Confidence:** High

#### [F-019] Async permission check runs synchronously via `.GetAwaiter().GetResult()` (still present)
- **Category:** bad-pattern
- **Severity:** Medium
- **Location:** [CodeFlow.Api/Auth/CompanyPermissionChecker.cs:68-88](CodeFlow.Api/Auth/CompanyPermissionChecker.cs:68)
- **Finding:** Re-flagged: the 2026-04-22 review (F-011) noted blocking on async inside the auth handler; still present.
- **Impact:** Thread starvation under load and ignored cancellation tokens.
- **Suggested fix:** As previously suggested — async-end-to-end with explicit timeout, or pre-fetch in middleware.
- **Confidence:** High — verified against current code.

#### [F-020] Unbounded version caches still in place
- **Category:** bad-pattern (efficiency)
- **Severity:** Medium
- **Location:** [WorkflowRepository.cs:7-10,34-35,164-165](CodeFlow.Persistence/WorkflowRepository.cs:7) and [AgentConfigRepository.cs:8-10,36-37,84-85](CodeFlow.Persistence/AgentConfigRepository.cs:8)
- **Finding:** Re-flagged: the 2026-04-22 review (F-013) flagged the unbounded `ConcurrentDictionary` version caches; the current code has switched the caches to `MemoryCache` with size limits — the unbounded growth concern is mitigated, but per F-006 the duplication concern remains.
- **Impact:** Resolved for memory growth; remains as duplicated infrastructure (see F-006).
- **Suggested fix:** Roll up into F-006.
- **Confidence:** High — verified against current code; lower the severity of the original 2026-04-22 finding.

## Low findings

#### [F-021] `ValidateScriptRequest.DeclaredPorts` accepted but ignored
- **Category:** dead-code
- **Severity:** Low
- **Location:** [CodeFlow.Api/Endpoints/WorkflowsEndpoints.cs:279](CodeFlow.Api/Endpoints/WorkflowsEndpoints.cs:279)
- **Finding:** Re-flagged from the 2026-04-22 review (F-014). The endpoint passes `Array.Empty<string>()` to the validator instead of the request's `DeclaredPorts`.
- **Impact:** Misleading API contract — callers can think they're validating against ports that the server never reads.
- **Suggested fix:** Either thread `request.DeclaredPorts` into the validator or remove the field from the request DTO.
- **Confidence:** High

#### [F-022] `WorkflowsEndpoints.PackageImportWritePolicies` literal in the endpoint file
- **Category:** bad-pattern
- **Severity:** Low
- **Location:** [WorkflowsEndpoints.cs:23-30](CodeFlow.Api/Endpoints/WorkflowsEndpoints.cs:23)
- **Finding:** Five policy names hardcoded as a `string[]` inside the endpoint class.
- **Impact:** Policy changes require touching this file; not a hazard, just a small scope leak.
- **Suggested fix:** Move into the auth setup module (where the policies are registered) and reference by named constant.
- **Confidence:** High

#### [F-023] Defensive `Logic`-node check in `ResolveSourcePortAsync` is unreachable
- **Category:** dead-code
- **Severity:** Low
- **Location:** [WorkflowSagaStateMachine.cs:1018](CodeFlow.Orchestration/WorkflowSagaStateMachine.cs:1018)
- **Finding:** Early-return when `fromNode.Kind == WorkflowNodeKind.Logic`, but `ResolveSourcePortAsync` is only called from `RouteCompletionAsync` on `AgentInvocationCompleted`, and Logic nodes are resolved through `ResolveTargetThroughLogicChainAsync` before any agent invocation is published. Logic nodes never appear here.
- **Impact:** Misleading — suggests Logic nodes can emit completions, which they cannot.
- **Suggested fix:** Remove the branch and add a debug-assertion if you want belt-and-braces.
- **Confidence:** Medium — confirm with a one-line test that asserts no `AgentInvocationCompleted` carries `FromNodeId` of a Logic node.

#### [F-024] Swarm early-termination flag is published but not consumed downstream
- **Category:** dead-code (partial)
- **Severity:** Low
- **Location:** [WorkflowSagaStateMachine.Swarm.cs:221,295-300,359-363](CodeFlow.Orchestration/WorkflowSagaStateMachine.Swarm.cs:221)
- **Finding:** `SwarmInvocationContext.EarlyTerminated` is set on the synthesizer dispatch when the token budget is blown, and on contributors as `false` always. Nothing downstream reads the flag — agents see it via the contract but the runtime/UI doesn't surface it.
- **Impact:** Wired-but-mute diagnostic field; if the intent was for the synthesizer prompt to react to early termination, the loop is incomplete.
- **Suggested fix:** Either consume it in the synthesizer's prompt template / a runtime emit, or document it as agent-visible only and remove the contributor-side `false` assignment.
- **Confidence:** Medium — needs a product call on whether this was meant to be plumbed further.

## Informational

#### [F-025] No production `NotImplementedException` / `TODO` / `FIXME` / `HACK` in backend code
- **Category:** info
- **Severity:** Info
- **Location:** Backend projects (`CodeFlow.{Api,Orchestration,Runtime,Persistence,Host,Worker,Contracts}`)
- **Finding:** A repo-wide grep found zero `throw new NotImplementedException` and zero unresolved `TODO/FIXME/HACK` markers in production backend code. `NotSupportedException` appears only in test fakes (intentional — fake-interface members the test doesn't exercise) and one `PlatformNotSupportedException` catch in `FileSystemArtifactStore` (legitimate platform guard). The `TODO` comments in [`WorkflowTemplates/SetupLoopFinalizeTemplate.cs`](CodeFlow.Api/WorkflowTemplates/SetupLoopFinalizeTemplate.cs) and [`ReviewLoopPairTemplate.cs`](CodeFlow.Api/WorkflowTemplates/ReviewLoopPairTemplate.cs) are template content meant for the end-user to fill in, not in-code stubs.
- **Impact:** The only true unfinished-feature stub found in the backend is the Coordinator gate (F-026 below). This is unusually clean for a codebase this size.
- **Confidence:** High

#### [F-026] Swarm `Coordinator` protocol — enum-defined, contract-supported, runtime-refused
- **Category:** dead-code (planned)
- **Severity:** Info (already tracked)
- **Location:** [WorkflowSagaStateMachine.Swarm.cs:61-72](CodeFlow.Orchestration/WorkflowSagaStateMachine.Swarm.cs:61)
- **Finding:** The protocol constant (`SwarmProtocolCoordinator`, `:19`) and the contracts ([AgentInvokeRequested.cs:33,42](CodeFlow.Contracts/AgentInvokeRequested.cs:33)) advertise Coordinator as a valid option, but the saga refuses dispatch with: *"Coordinator runtime ships in sc-46 — re-save the workflow with protocol 'Sequential' to run it now."*
- **Impact:** Validation accepts a workflow that the runtime will reject — the failure mode is a clean runtime error, but it's still a foot-gun. Memory `project_swarm_node_epic.md` notes sc-46 is in review (PR #103); this gate should be removed when that lands. Calling it out so it doesn't get forgotten.
- **Suggested fix:** When PR #103 merges, delete the `if (protocol == Coordinator) return …` block and update the doc comment at the top of `Swarm.cs`. Add an early validation in `WorkflowValidator` that rejects Coordinator-protocol nodes until then so the failure surfaces at save time, not run time.
- **Confidence:** High

## Themes and systemic observations

**1. Two engines, one set of semantics.** The biggest single pattern issue in the backend is that `WorkflowSagaStateMachine` and `DryRunExecutor` independently implement workflow semantics — node dispatch, decision templating, retry context, P3/P4/P5 transforms — and have to be hand-kept in sync. Six of the High findings (F-001, F-002, F-007, F-008, F-009 plus the related F-010) are facets of this. The right fix is structural: extract per-kind dispatchers and the cross-cutting helpers (template renderer, retry-context builder, output-transform applier) into shared services that both engines call.

**2. Endpoint files have absorbed business logic.** The four largest API files (Traces 1.3k, WorkflowPackageImporter 1.0k, WorkflowValidator 0.9k, Workflows 0.9k) all suffer from inlined orchestration. The contributing patterns are uniform: no per-operation handler layer (F-004), exception-as-flow for "not found" (F-015), validator/importer overlap (F-013), and inconsistent error shapes (F-005). A single round of "extract a handler per operation, share validation and mapping helpers" would drop all four files significantly.

**3. Provider plumbing is not yet abstract.** Three LLM clients (OpenAI-compatible base + LM Studio derivative + Anthropic sibling) each carry their own retry/back-off (F-003), and two VCS providers carry their own error-normalisation (F-017). The cost will grow as new providers land — the right time to extract a base is before the third Bedrock/Vertex/Bitbucket addition forces a third copy.

**4. Stubs are very rare.** This is genuinely the cleanest codebase-wide stub picture I've seen in a while: zero `NotImplementedException` / `TODO` markers in production code (F-025), one named feature gate awaiting its sibling PR (F-026), one accepted-but-ignored DTO field (F-021), one published-but-unread diagnostic flag (F-024), and one defensive guard that's actually unreachable (F-023). The "unfinished work" lens is mostly a quiet finding.

## Coverage notes

- Reviewed only the backend `.cs` files under `CodeFlow.{Api,Orchestration,Runtime,Persistence,Host,Worker,Contracts}`. Test projects, EF migrations, the `bench/` and `design_handoff_*` trees, the Angular UI under `codeflow-ui/`, and the `.claude/worktrees/` shadow tree were excluded.
- Security was deliberately skipped per scope. The 2026-04-22 review (`CODE_REVIEW_2026-04-22.md`) and 2026-04-28 findings (`CODE_REVIEW_FINDINGS_2026-04-28.md`) cover that surface; F-019 and F-020 are the only re-flags here, both confirmed against current source.
- One earlier-review finding I would have re-flagged (the unbounded `ConcurrentDictionary` version caches) is no longer accurate — both repositories have moved to `MemoryCache` with size limits. The duplication concern remains and is now F-006.
- One agent-reported finding ("Swarm token-budget incomplete — counter never incremented") was dismissed after verification: [WorkflowSagaStateMachine.Swarm.cs:178-180](CodeFlow.Orchestration/WorkflowSagaStateMachine.Swarm.cs:178) does accumulate `message.TokenUsage.InputTokens + OutputTokens` correctly.
- Static review only. RabbitMQ/MariaDB integration, dynamic dispatch behaviour, and runtime-cost claims (e.g. F-003 retry behaviour) should be confirmed under load before any refactor that touches them.
