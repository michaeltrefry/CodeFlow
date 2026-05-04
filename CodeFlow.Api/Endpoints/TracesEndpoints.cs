using CodeFlow.Api.Auth;
using CodeFlow.Api.Dtos;
using CodeFlow.Api.TraceEvents;
using CodeFlow.Api.Validation;
using CodeFlow.Contracts;
using CodeFlow.Orchestration.Scripting;
using CodeFlow.Persistence;
using CodeFlow.Runtime.Authority;
using CodeFlow.Runtime.Authority.Preflight;
using CodeFlow.Runtime.Workspace;
using MassTransit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeFlow.Api.Endpoints;

public static class TracesEndpoints
{
    private static readonly string[] TerminalTraceStates = ["Completed", "Failed"];

    public static IEndpointRouteBuilder MapTracesEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/api/traces");

        group.MapGet("/", ListTracesAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.TracesRead);

        group.MapGet("/{id:guid}", GetTraceAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.TracesRead);

        group.MapGet("/{id:guid}/descendants", GetTraceDescendantsAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.TracesRead);

        group.MapPost("/", CreateTraceAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.TracesWrite);

        group.MapGet("/{id:guid}/stream", StreamTraceAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.TracesRead);

        group.MapGet("/{id:guid}/artifact", GetArtifactAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.TracesRead);

        group.MapGet("/hitl/pending", ListPendingHitlAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.TracesRead);

        group.MapPost("/{id:guid}/hitl-decision", SubmitHitlDecisionAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.HitlWrite);

        group.MapPost("/{id:guid}/terminate", TerminateTraceAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.TracesWrite);

        group.MapDelete("/{id:guid}", DeleteTraceAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.TracesWrite);

        group.MapPost("/bulk-delete", BulkDeleteTracesAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.TracesWrite);

        // Replay-with-edit (T2-B): substitution-only re-run of a recorded trace via DryRunExecutor.
        // Read-only on the original saga — the replay is ephemeral and lives only in the response —
        // so TracesRead is sufficient.
        group.MapPost("/{id:guid}/replay", TracesReplayEndpoints.ReplayTraceAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.TracesRead);

        // Token Usage Tracking [Slice 5]: aggregation API. Returns rollups at every level
        // (per-call, per-invocation, per-node, per-scope, per-trace) for a single trace, with
        // provider+model breakdowns. Read-only — TracesRead.
        group.MapGet("/{id:guid}/token-usage", TraceTokenUsageEndpoints.GetTraceTokenUsageAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.TracesRead);

        // sc-285: append-only refusal stream for a trace or assistant conversation. Read-only —
        // TracesRead. Refusals captured by tools (sc-270 workspace mutation today; envelope and
        // gates as those land) become first-class evidence rather than missing execution.
        group.MapGet("/{id:guid}/refusals", TraceRefusalsEndpoints.GetTraceRefusalsAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.TracesRead);

        // sc-271: portable trace evidence bundle. Read-only zip of saga state + decisions +
        // refusals + authority snapshots + token usage + every referenced artifact's bytes,
        // each pinned by SHA-256. Manifest schema is versioned for forward-compat.
        group.MapGet("/{id:guid}/bundle", TraceBundle.TraceBundleEndpoints.GetTraceBundleAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.TracesRead);

        // sc-271 PR2: manifest-only sibling of the bundle endpoint. Returns the same JSON
        // structure as `manifest.json` inside the zip but skips artifact byte packaging,
        // so the trace inspector can render a bundle-composition panel cheaply.
        group.MapGet("/{id:guid}/bundle/manifest", TraceBundle.TraceBundleEndpoints.GetTraceBundleManifestAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.TracesRead);

        return routes;
    }

    private static async Task<IResult> ListTracesAsync(
        CodeFlowDbContext dbContext,
        CancellationToken cancellationToken,
        string? workflowKey = null,
        string? state = null,
        int take = 50)
    {
        take = Math.Clamp(take, 1, 500);

        var query = dbContext.WorkflowSagas.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(workflowKey))
        {
            var normalized = workflowKey.Trim();
            query = query.Where(saga => saga.WorkflowKey == normalized);
        }

        if (!string.IsNullOrWhiteSpace(state))
        {
            var normalized = state.Trim();
            query = query.Where(saga => saga.CurrentState == normalized);
        }

        var sagas = await query
            .OrderByDescending(saga => saga.UpdatedAtUtc)
            .Take(take)
            .ToListAsync(cancellationToken);

        return Results.Ok(sagas.Select(MapSummary).ToArray());
    }

    private static async Task<IResult> GetTraceAsync(
        Guid id,
        CodeFlowDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var saga = await dbContext.WorkflowSagas
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.TraceId == id, cancellationToken);

        if (saga is null)
        {
            return Results.NotFound();
        }

        var subtreeSagas = await CollectSubtreeSagasAsync(dbContext, saga, cancellationToken);
        var subtreeTraceIds = subtreeSagas.Select(s => s.TraceId).ToArray();
        var subflowPaths = BuildSubflowPaths(saga.TraceId, subtreeSagas);

        var pendingHitl = await dbContext.HitlTasks
            .AsNoTracking()
            .Where(task => subtreeTraceIds.Contains(task.TraceId) && task.State == HitlTaskState.Pending)
            .OrderBy(task => task.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        var decisions = await dbContext.WorkflowSagaDecisions
            .AsNoTracking()
            .Where(d => d.SagaCorrelationId == saga.CorrelationId)
            .OrderBy(d => d.Ordinal)
            .ToListAsync(cancellationToken);

        var logicEvaluations = await dbContext.WorkflowSagaLogicEvaluations
            .AsNoTracking()
            .Where(e => e.SagaCorrelationId == saga.CorrelationId)
            .OrderBy(e => e.Ordinal)
            .ToListAsync(cancellationToken);

        var verdictSources = await ResolveVerdictSourcesAsync(
            dbContext, decisions.Select(d => d.AgentKey), cancellationToken);

        var detail = MapDetail(
            saga,
            decisions,
            logicEvaluations,
            pendingHitl,
            subflowPaths,
            verdictSources);

        return Results.Ok(detail);
    }

    private static async Task<IResult> GetTraceDescendantsAsync(
        Guid id,
        CodeFlowDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var saga = await dbContext.WorkflowSagas
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.TraceId == id, cancellationToken);

        if (saga is null)
        {
            return Results.NotFound();
        }

        var subtreeSagas = await CollectSubtreeSagasAsync(dbContext, saga, cancellationToken);
        var descendants = subtreeSagas
            .Where(s => s.TraceId != saga.TraceId)
            .OrderBy(s => s.SubflowDepth)
            .ThenBy(s => s.CreatedAtUtc)
            .ToArray();

        if (descendants.Length == 0)
        {
            return Results.Ok(Array.Empty<TraceDescendantDto>());
        }

        var correlationIds = descendants.Select(s => s.CorrelationId).ToArray();
        var traceIds = descendants.Select(s => s.TraceId).ToArray();

        var decisions = await dbContext.WorkflowSagaDecisions
            .AsNoTracking()
            .Where(d => correlationIds.Contains(d.SagaCorrelationId))
            .OrderBy(d => d.Ordinal)
            .ToListAsync(cancellationToken);

        var logicEvaluations = await dbContext.WorkflowSagaLogicEvaluations
            .AsNoTracking()
            .Where(e => correlationIds.Contains(e.SagaCorrelationId))
            .OrderBy(e => e.Ordinal)
            .ToListAsync(cancellationToken);

        var pendingHitl = await dbContext.HitlTasks
            .AsNoTracking()
            .Where(task => traceIds.Contains(task.TraceId) && task.State == HitlTaskState.Pending)
            .OrderBy(task => task.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        var subflowPaths = BuildSubflowPaths(saga.TraceId, subtreeSagas);
        var decisionsByCorrelationId = decisions
            .GroupBy(d => d.SagaCorrelationId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<WorkflowSagaDecisionEntity>)group.ToArray());
        var evaluationsByCorrelationId = logicEvaluations
            .GroupBy(e => e.SagaCorrelationId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<WorkflowSagaLogicEvaluationEntity>)group.ToArray());
        var hitlByTraceId = pendingHitl
            .GroupBy(task => task.TraceId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<HitlTaskEntity>)group.ToArray());

        var verdictSources = await ResolveVerdictSourcesAsync(
            dbContext, decisions.Select(d => d.AgentKey), cancellationToken);

        var response = descendants
            .Select(descendant => new TraceDescendantDto(
                Summary: MapSummary(descendant),
                Detail: MapDetail(
                    descendant,
                    decisionsByCorrelationId.GetValueOrDefault(descendant.CorrelationId) ?? Array.Empty<WorkflowSagaDecisionEntity>(),
                    evaluationsByCorrelationId.GetValueOrDefault(descendant.CorrelationId) ?? Array.Empty<WorkflowSagaLogicEvaluationEntity>(),
                    hitlByTraceId.GetValueOrDefault(descendant.TraceId) ?? Array.Empty<HitlTaskEntity>(),
                    subflowPaths,
                    verdictSources)))
            .ToArray();

        return Results.Ok(response);
    }

    private static async Task<IResult> CreateTraceAsync(
        CreateTraceRequest request,
        IWorkflowRepository workflowRepository,
        IAgentConfigRepository agentRepository,
        IArtifactStore artifactStore,
        IPublishEndpoint publishEndpoint,
        CodeFlowDbContext dbContext,
        ICurrentUser currentUser,
        LogicNodeScriptHost scriptHost,
        IOptions<WorkspaceOptions> workspaceOptions,
        IRefusalEventSink refusalSink,
        IIntentClarityAssessor preflightAssessor,
        IOptions<PreflightOptions> preflightOptions,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.WorkflowKey))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["workflowKey"] = new[] { "workflowKey is required." }
            });
        }

        if (string.IsNullOrWhiteSpace(request.Input))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["input"] = new[] { "input is required." }
            });
        }

        // sc-274 phase 3 — ambiguity preflight runs BEFORE workflow lookup / artifact write /
        // saga publish. Refusing here saves all of that work and gives the launcher a focused
        // clarification list instead of letting the saga fail downstream with a vague Start
        // agent output. Mode is inferred from the de-facto code-aware-workflows convention
        // (presence of a "repositories" key in Inputs); workflows without that key are
        // assumed to be greenfield drafting. Disabled-mode and assessor failures fall through
        // to the normal launch path — preflight is observability for ambiguity, never a hard
        // barrier when the assessor itself misbehaves.
        if (preflightOptions.Value.Enabled)
        {
            var hasRepositoriesInput = request.Inputs is not null
                && request.Inputs.ContainsKey("repositories");
            var preflightMode = hasRepositoriesInput
                ? PreflightMode.BrownfieldChange
                : PreflightMode.GreenfieldDraft;

            IntentClarityAssessment? assessment = null;
            try
            {
                var preflightInput = new WorkflowLaunchPreflightInput(
                    Input: request.Input,
                    HasRepositoriesInput: hasRepositoriesInput,
                    WorkflowKey: request.WorkflowKey);
                assessment = preflightAssessor.Assess(preflightMode, preflightInput);
            }
            catch
            {
                // Preflight failure is observability-only — never block the launch flow because
                // of an assessor bug. The workflow lookup + saga publish still run below.
            }

            if (assessment is { IsClear: false })
            {
                await EmitWorkflowPreflightRefusalAsync(
                    refusalSink, request.WorkflowKey, assessment, cancellationToken);
                return Results.Json(
                    BuildWorkflowPreflightResponse(request.WorkflowKey, assessment),
                    statusCode: StatusCodes.Status422UnprocessableEntity);
            }
        }

        Workflow workflow;
        if (request.WorkflowVersion is int version)
        {
            var resolved = await workflowRepository.TryGetAsync(request.WorkflowKey, version, cancellationToken);
            if (resolved is null)
            {
                return ApiResults.NotFound($"Workflow '{request.WorkflowKey}' version {request.WorkflowVersion} not found.");
            }
            workflow = resolved;
        }
        else
        {
            var latest = await workflowRepository.GetLatestAsync(request.WorkflowKey, cancellationToken);
            if (latest is null)
            {
                return ApiResults.NotFound($"Workflow '{request.WorkflowKey}' not found.");
            }
            workflow = latest;
        }

        if (workflow.IsRetired)
        {
            return ApiResults.Conflict($"Workflow '{workflow.Key}' is retired and cannot be used for new traces.");
        }

        var startNode = workflow.StartNode;
        if (string.IsNullOrWhiteSpace(startNode.AgentKey))
        {
            return Results.Problem("Workflow start node has no AgentKey configured.", statusCode: 500);
        }

        var resolvedInputsResult = ResolveContextInputs(workflow, request.Inputs);
        if (resolvedInputsResult.Error is not null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["inputs"] = new[] { resolvedInputsResult.Error }
            }, statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        var startAgentVersion = startNode.AgentVersion
            ?? await agentRepository.GetLatestVersionAsync(startNode.AgentKey, cancellationToken);

        var traceId = Guid.NewGuid();
        var roundId = Guid.NewGuid();

        await using var inputStream = new MemoryStream(Encoding.UTF8.GetBytes(request.Input));
        var inputRef = await artifactStore.WriteAsync(
            inputStream,
            new ArtifactMetadata(
                TraceId: traceId,
                RoundId: roundId,
                ArtifactId: Guid.NewGuid(),
                ContentType: "text/plain",
                FileName: request.InputFileName ?? "input.txt"),
            cancellationToken);

        // Seed framework-managed workflow variables before the start-node input script runs so
        // that scripts and the start agent's prompt template can reference them. These keys are
        // listed in `ProtectedVariables.ReservedKeys` and cannot be overwritten by scripts or
        // agents. Top-level traces only — child sagas inherit via the workflow snapshot, which
        // means subflow and ReviewLoop children see the *parent's* traceId (the right answer for
        // branch naming and any other identity-anchored work).
        var seededWorkflowVars = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        seededWorkflowVars["traceId"] = JsonDocument.Parse(
            JsonSerializer.Serialize(traceId.ToString("N"))).RootElement.Clone();

        // The per-trace working-directory root is locked to WorkspaceOptions.WorkingDirectoryRoot
        // (default `/workspace`) — a deployment-level constant matched by a shared host
        // volume on both api and worker. Failure to create the per-trace directory is fatal: it
        // means the operator hasn't mounted the volume on this side, and proceeding would let
        // code-aware agents fail later with the path-rejection symptoms the runtime can't fully
        // diagnose. Override via `Workspace__WorkingDirectoryRoot` for non-container dev.
        var traceWorkDir = Path.Combine(workspaceOptions.Value.WorkingDirectoryRoot, traceId.ToString("N"));
        try
        {
            Directory.CreateDirectory(traceWorkDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Results.Problem(
                detail: $"Failed to create per-trace working directory '{traceWorkDir}': {ex.Message}. "
                    + $"Ensure '{workspaceOptions.Value.WorkingDirectoryRoot}' is mounted as a writable shared volume on the api and worker containers.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
        // sc-604 Phase 3: the legacy `workflow.workDir` alias is gone. The canonical
        // author-facing variable is `workflow.traceWorkDir`; the runtime source of truth is
        // the saga's typed TraceWorkDir field (set on the published AgentInvokeRequested
        // below).
        seededWorkflowVars["traceWorkDir"] = JsonDocument.Parse(JsonSerializer.Serialize(traceWorkDir)).RootElement.Clone();

        // Mid-workflow dispatches run a node's InputScript via the saga's TryEvaluateInputScriptAsync
        // helper, but a top-level Start has no saga yet at this point, so we evaluate the script
        // here. The corresponding LogicEvaluationRecord is intentionally dropped — capturing it
        // would require either changing the AgentInvokeRequested contract or pre-creating saga
        // state from the endpoint. Per-saga hooks still record evaluations for child Start
        // dispatches and every other node.
        var effectiveInputRef = inputRef;
        var effectiveContextInputs = resolvedInputsResult.Values;
        // sc-607: per-trace `repositories` is workflow-context state, not local-context. The
        // workflow-input convention still works at the authoring layer (declare `repositories`
        // as a Json input, validated for shape), but at launch we route the resolved value
        // into seededWorkflowVars so the saga lifts it from `workflow.*` rather than `context.*`.
        // Removing it from effectiveContextInputs avoids a confusing duplicate appearance under
        // both `workflow.repositories` and `context.repositories` in templates and scripts.
        if (effectiveContextInputs.TryGetValue(WorkflowValidator.RepositoriesInputKey, out var resolvedRepositories))
        {
            seededWorkflowVars[WorkflowValidator.RepositoriesInputKey] = resolvedRepositories;
            var trimmed = new Dictionary<string, JsonElement>(effectiveContextInputs, StringComparer.Ordinal);
            trimmed.Remove(WorkflowValidator.RepositoriesInputKey);
            effectiveContextInputs = trimmed;
        }
        if (!string.IsNullOrWhiteSpace(startNode.InputScript))
        {
            var artifactJson = await ReadArtifactAsJsonAsync(artifactStore, inputRef, cancellationToken);
            var eval = scriptHost.Evaluate(
                workflowKey: workflow.Key,
                workflowVersion: workflow.Version,
                nodeId: startNode.Id,
                script: startNode.InputScript!,
                declaredPorts: startNode.OutputPorts,
                input: artifactJson,
                context: resolvedInputsResult.Values,
                cancellationToken: cancellationToken,
                workflow: seededWorkflowVars,
                allowInputOverride: true,
                requireSetNodePath: false);

            if (eval.Failure is not null)
            {
                return Results.Problem(
                    detail: $"Input script for start node {startNode.Id} failed ({eval.Failure}): {eval.FailureMessage}",
                    statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            if (!string.IsNullOrEmpty(eval.InputOverride))
            {
                await using var overrideStream = new MemoryStream(Encoding.UTF8.GetBytes(eval.InputOverride!));
                effectiveInputRef = await artifactStore.WriteAsync(
                    overrideStream,
                    new ArtifactMetadata(
                        TraceId: traceId,
                        RoundId: roundId,
                        ArtifactId: Guid.NewGuid(),
                        ContentType: "text/plain",
                        FileName: $"{startNode.AgentKey}-scripted-input.txt"),
                    cancellationToken);
            }

            // Apply setContext / setWorkflow writes from the script onto the published message.
            // Without this, scripts can override the input artifact but cannot seed shared state
            // for the start agent — the saga's own input-script handler does the same merge, so
            // doing it here keeps top-level Start parity with mid-workflow dispatches.
            if (eval.ContextUpdates.Count > 0)
            {
                var merged = new Dictionary<string, JsonElement>(effectiveContextInputs, StringComparer.Ordinal);
                foreach (var (key, value) in eval.ContextUpdates)
                {
                    merged[key] = value;
                }
                effectiveContextInputs = merged;
            }

            if (eval.WorkflowUpdates.Count > 0)
            {
                foreach (var (key, value) in eval.WorkflowUpdates)
                {
                    seededWorkflowVars[key] = value;
                }
            }
        }

        IReadOnlyDictionary<string, JsonElement>? effectiveWorkflowContext = seededWorkflowVars.Count > 0
            ? seededWorkflowVars
            : null;

        await publishEndpoint.Publish(
            new AgentInvokeRequested(
                TraceId: traceId,
                RoundId: roundId,
                WorkflowKey: workflow.Key,
                WorkflowVersion: workflow.Version,
                NodeId: startNode.Id,
                AgentKey: startNode.AgentKey,
                AgentVersion: startAgentVersion,
                InputRef: effectiveInputRef,
                ContextInputs: effectiveContextInputs,
                CorrelationHeaders: new Dictionary<string, string>
                {
                    ["x-submitted-by"] = currentUser.Id ?? "unknown"
                },
                WorkflowContext: effectiveWorkflowContext,
                // sc-602: typed per-trace workspace anchor. The saga prefers this over the
                // legacy `workflow.workDir` bag-key fallback in ApplyInitialRequest; once Phase 3
                // drops the bag entry, this becomes the only source.
                TraceWorkDir: traceWorkDir),
            cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.Created($"/api/traces/{traceId}", new CreateTraceResponse(traceId));
    }

    private static async Task<IResult> TerminateTraceAsync(
        Guid id,
        CodeFlowDbContext dbContext,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        var saga = await dbContext.WorkflowSagas
            .FirstOrDefaultAsync(s => s.TraceId == id, cancellationToken);

        if (saga is null)
        {
            return Results.NotFound();
        }

        if (!string.Equals(saga.CurrentState, "Running", StringComparison.Ordinal))
        {
            return Results.Conflict(new
            {
                error = $"Trace {id} is not running and cannot be terminated."
            });
        }

        var nowUtc = DateTime.UtcNow;
        saga.CurrentState = "Failed";
        saga.FailureReason = "Terminated by user.";
        saga.UpdatedAtUtc = nowUtc;

        var pendingTasks = await dbContext.HitlTasks
            .Where(task => task.TraceId == id && task.State == HitlTaskState.Pending)
            .ToListAsync(cancellationToken);

        foreach (var task in pendingTasks)
        {
            task.State = HitlTaskState.Cancelled;
            task.DeciderId = currentUser.Id ?? "unknown";
            task.DecidedAtUtc = nowUtc;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteTraceAsync(
        Guid id,
        CodeFlowDbContext dbContext,
        IOptions<WorkspaceOptions> workspaceOptions,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var saga = await dbContext.WorkflowSagas
            .FirstOrDefaultAsync(s => s.TraceId == id, cancellationToken);

        if (saga is null)
        {
            return Results.NotFound();
        }

        if (string.Equals(saga.CurrentState, "Running", StringComparison.Ordinal))
        {
            return Results.Conflict(new
            {
                error = $"Trace {id} is still running. Terminate it before deleting."
            });
        }

        await DeleteTracesAsync(dbContext, [saga], workspaceOptions, loggerFactory, cancellationToken);

        return Results.NoContent();
    }

    private static async Task<IResult> BulkDeleteTracesAsync(
        BulkDeleteTracesRequest request,
        CodeFlowDbContext dbContext,
        IOptions<WorkspaceOptions> workspaceOptions,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (request.OlderThanDays < 1 || request.OlderThanDays > 3650)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["olderThanDays"] = ["olderThanDays must be between 1 and 3650."]
            });
        }

        string? normalizedState = null;
        if (!string.IsNullOrWhiteSpace(request.State))
        {
            normalizedState = request.State.Trim();
            if (!TerminalTraceStates.Contains(normalizedState, StringComparer.Ordinal))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["state"] = [$"state must be one of: {string.Join(", ", TerminalTraceStates)}."]
                });
            }
        }

        var cutoffUtc = DateTime.UtcNow.AddDays(-request.OlderThanDays);
        var query = dbContext.WorkflowSagas
            .Where(saga => TerminalTraceStates.Contains(saga.CurrentState)
                && saga.UpdatedAtUtc <= cutoffUtc);

        if (normalizedState is not null)
        {
            query = query.Where(saga => saga.CurrentState == normalizedState);
        }

        var sagas = await query.ToListAsync(cancellationToken);
        var deletedCount = await DeleteTracesAsync(
            dbContext, sagas, workspaceOptions, loggerFactory, cancellationToken);

        return Results.Ok(new BulkDeleteTracesResponse(deletedCount));
    }

    private static (IReadOnlyDictionary<string, JsonElement> Values, string? Error) ResolveContextInputs(
        Workflow workflow,
        IReadOnlyDictionary<string, JsonElement>? supplied)
    {
        if (workflow.Inputs.Count == 0 && (supplied is null || supplied.Count == 0))
        {
            return (new Dictionary<string, JsonElement>(StringComparer.Ordinal), null);
        }

        var resolved = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var providedKeys = new HashSet<string>(supplied?.Keys ?? Enumerable.Empty<string>(), StringComparer.Ordinal);

        foreach (var definition in workflow.Inputs.OrderBy(input => input.Ordinal))
        {
            if (supplied is not null && supplied.TryGetValue(definition.Key, out var value))
            {
                if (string.Equals(definition.Key, WorkflowValidator.RepositoriesInputKey, StringComparison.Ordinal)
                    && definition.Kind == WorkflowInputKind.Json)
                {
                    var shapeError = WorkflowValidator.ValidateRepositoriesShape(value);
                    if (shapeError is not null)
                    {
                        return (resolved,
                            $"Input 'repositories' has an invalid shape: {shapeError}");
                    }
                }

                resolved[definition.Key] = value.Clone();
                continue;
            }

            if (!string.IsNullOrWhiteSpace(definition.DefaultValueJson))
            {
                using var document = JsonDocument.Parse(definition.DefaultValueJson);
                resolved[definition.Key] = document.RootElement.Clone();
                continue;
            }

            if (definition.Required)
            {
                return (resolved, $"Required input '{definition.Key}' was not supplied and has no default.");
            }
        }

        var undeclared = providedKeys
            .Where(key => !workflow.Inputs.Any(i => string.Equals(i.Key, key, StringComparison.Ordinal)))
            .ToArray();

        if (undeclared.Length > 0)
        {
            return (resolved, $"Unknown input(s) supplied: {string.Join(", ", undeclared)}.");
        }

        return (resolved, null);
    }

    private static async Task<IResult> GetArtifactAsync(
        Guid id,
        string uri,
        IArtifactStore artifactStore,
        CodeFlowDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(uri) || !Uri.TryCreate(uri, UriKind.Absolute, out var artifactUri))
        {
            return ApiResults.BadRequest("A valid artifact URI is required.");
        }

        ArtifactMetadata metadata;
        try
        {
            metadata = await artifactStore.GetMetadataAsync(artifactUri, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return Results.NotFound();
        }
        catch (ArgumentException)
        {
            return ApiResults.BadRequest("The artifact URI is not valid for this store.");
        }

        // The authoritative owner is the trace whose saga wrote the artifact (metadata.TraceId).
        // But when Subflow/ReviewLoop nodes surface a child's output on the parent trace's
        // timeline, the artifact's owning trace is a *descendant* of the trace being viewed. We
        // walk down through parent_trace_id to decide whether the requested trace is an ancestor
        // of the artifact's owning trace, and only then serve it. Sibling and unrelated traces
        // still 404. Bounded by MaxSubflowDepth so there's no risk of unbounded recursion.
        if (metadata.TraceId != id
            && !await IsDescendantTraceAsync(dbContext, id, metadata.TraceId, cancellationToken))
        {
            return Results.NotFound();
        }

        Stream content;
        try
        {
            content = await artifactStore.ReadAsync(artifactUri, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return Results.NotFound();
        }

        return Results.Stream(
            content,
            contentType: metadata.ContentType ?? "application/octet-stream",
            fileDownloadName: metadata.FileName);
    }

    private static async Task StreamTraceAsync(
        Guid id,
        HttpContext httpContext,
        CodeFlowDbContext dbContext,
        TraceEventBroker broker,
        CancellationToken cancellationToken)
    {
        httpContext.Response.Headers.ContentType = "text/event-stream";
        httpContext.Response.Headers.CacheControl = "no-cache";
        httpContext.Response.Headers.Connection = "keep-alive";
        httpContext.Response.Headers["X-Accel-Buffering"] = "no";

        await httpContext.Response.WriteAsync(": connected\n\n", cancellationToken);
        await httpContext.Response.Body.FlushAsync(cancellationToken);

        // Register the live subscription BEFORE replaying existing decisions. Otherwise a
        // decision that commits during the replay query (e.g., the Start node, which lands in
        // the first second of the trace) would fan out to no subscriber and be lost — the
        // client would see nothing happen until the next decision in the workflow. Subscribing
        // up front means those events queue in the channel and we drain them after the replay.
        // The client already deduplicates by `${roundId}-${agentKey}-${kind}-${timestamp}`, so
        // the rare overlap between a replayed and live event for the same decision is harmless.
        using var subscription = broker.Subscribe(id);

        await WriteExistingDecisionsAsync(httpContext, dbContext, id, cancellationToken);

        await foreach (var traceEvent in subscription.ReadAllAsync(cancellationToken))
        {
            await WriteEventAsync(httpContext, traceEvent, cancellationToken);
        }
    }

    private static async Task WriteExistingDecisionsAsync(
        HttpContext httpContext,
        CodeFlowDbContext dbContext,
        Guid traceId,
        CancellationToken cancellationToken)
    {
        var decisions = await dbContext.WorkflowSagaDecisions
            .AsNoTracking()
            .Where(d => d.TraceId == traceId)
            .OrderBy(d => d.Ordinal)
            .ToListAsync(cancellationToken);

        foreach (var decision in decisions)
        {
            var traceEvent = new TraceEvent(
                TraceId: decision.TraceId,
                RoundId: decision.RoundId,
                Kind: TraceEventKind.Completed,
                AgentKey: decision.AgentKey,
                AgentVersion: decision.AgentVersion,
                OutputRef: null,
                InputRef: null,
                Decision: decision.Decision,
                DecisionPayload: ParsePayload(decision.DecisionPayloadJson),
                TimestampUtc: DateTime.SpecifyKind(decision.RecordedAtUtc, DateTimeKind.Utc));

            await WriteEventAsync(httpContext, traceEvent, cancellationToken);
        }
    }

    private static async Task WriteEventAsync(
        HttpContext httpContext,
        TraceEvent traceEvent,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(traceEvent, TraceEventJson.Options);
        var frame = $"event: {traceEvent.Kind.ToString().ToLowerInvariant()}\ndata: {json}\n\n";
        await httpContext.Response.WriteAsync(frame, cancellationToken);
        await httpContext.Response.Body.FlushAsync(cancellationToken);
    }

    private static async Task<IResult> ListPendingHitlAsync(
        CodeFlowDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var tasks = await dbContext.HitlTasks
            .AsNoTracking()
            .Where(task => task.State == HitlTaskState.Pending)
            .OrderBy(task => task.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        return Results.Ok(tasks.Select(MapHitl).ToArray());
    }

    private static async Task<IResult> SubmitHitlDecisionAsync(
        Guid id,
        HitlDecisionRequest request,
        CodeFlowDbContext dbContext,
        IArtifactStore artifactStore,
        IPublishEndpoint publishEndpoint,
        IAgentConfigRepository agentConfigRepo,
        CodeFlow.Runtime.IScribanTemplateRenderer templateRenderer,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        var task = await dbContext.HitlTasks
            .FirstOrDefaultAsync(
                t => t.TraceId == id && t.State == HitlTaskState.Pending,
                cancellationToken);

        if (task is null)
        {
            return ApiResults.NotFound("No pending HITL task for this trace.");
        }

        var startedAt = DateTimeOffset.UtcNow;

        if (string.IsNullOrWhiteSpace(request.OutputPortName))
        {
            return ApiResults.BadRequest("OutputPortName is required.");
        }

        var outputPortName = request.OutputPortName.Trim();
        var decisionPayload = BuildDecisionPayload(request, outputPortName);

        // Resolve a per-decision output template (if the agent declares one) and render server-side.
        // Fall back to the client-rendered OutputText / BuildDefaultOutput when no template matches,
        // preserving existing HITL flows.
        var renderedOutput = await RenderHitlDecisionOutputAsync(
            agentConfigRepo,
            templateRenderer,
            dbContext,
            task,
            request,
            outputPortName,
            cancellationToken);

        if (renderedOutput.Failure is not null)
        {
            return ApiResults.UnprocessableEntity(renderedOutput.Failure);
        }

        var outputText = renderedOutput.Text
            ?? request.OutputText
            ?? BuildDefaultOutput(request);

        await using var outputStream = new MemoryStream(Encoding.UTF8.GetBytes(outputText));
        var outputRef = await artifactStore.WriteAsync(
            outputStream,
            new ArtifactMetadata(
                TraceId: task.TraceId,
                RoundId: task.RoundId,
                ArtifactId: Guid.NewGuid(),
                ContentType: "text/plain",
                FileName: $"{task.AgentKey}-hitl-output.txt"),
            cancellationToken);

        task.State = HitlTaskState.Decided;
        task.Decision = outputPortName;
        task.DecisionPayloadJson = decisionPayload.GetRawText();
        task.DeciderId = currentUser.Id;
        task.DecidedAtUtc = DateTime.UtcNow;

        await publishEndpoint.Publish(
            new AgentInvocationCompleted(
                TraceId: task.TraceId,
                RoundId: task.RoundId,
                FromNodeId: task.NodeId,
                AgentKey: task.AgentKey,
                AgentVersion: task.AgentVersion,
                OutputPortName: outputPortName,
                OutputRef: outputRef,
                DecisionPayload: decisionPayload,
                Duration: DateTimeOffset.UtcNow - startedAt,
                TokenUsage: new Contracts.TokenUsage(0, 0, 0)),
            cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.Ok(new { taskId = task.Id });
    }

    private record struct HitlRenderResult(string? Text, string? Failure);

    private static async Task<HitlRenderResult> RenderHitlDecisionOutputAsync(
        IAgentConfigRepository agentConfigRepo,
        CodeFlow.Runtime.IScribanTemplateRenderer templateRenderer,
        CodeFlowDbContext dbContext,
        HitlTaskEntity task,
        HitlDecisionRequest request,
        string outputPortName,
        CancellationToken cancellationToken)
    {
        var agentConfig = await agentConfigRepo.TryGetAsync(task.AgentKey, task.AgentVersion, cancellationToken);
        if (agentConfig is null)
        {
            return new HitlRenderResult(null, null);
        }

        var templates = agentConfig.Configuration.DecisionOutputTemplates;
        if (templates is null || templates.Count == 0)
        {
            return new HitlRenderResult(null, null);
        }

        string? template = null;
        foreach (var entry in templates)
        {
            if (string.Equals(entry.Key, outputPortName, StringComparison.OrdinalIgnoreCase))
            {
                template = entry.Value;
                break;
            }
        }
        if (template is null)
        {
            templates.TryGetValue("*", out template);
        }
        if (template is null)
        {
            return new HitlRenderResult(null, null);
        }

        var saga = await dbContext.WorkflowSagas
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.TraceId == task.TraceId, cancellationToken);
        var contextInputs = DeserializeInputsJson(saga?.InputsJson);
        var workflowInputs = DeserializeInputsJson(saga?.WorkflowInputsJson);

        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (request.FieldValues is not null)
        {
            foreach (var (key, value) in request.FieldValues)
            {
                if (!string.IsNullOrWhiteSpace(key))
                {
                    fields[key] = value;
                }
            }
        }

        var decisionName = outputPortName;

        var scope = CodeFlow.Orchestration.DecisionOutputTemplateContext.BuildForHitl(
            decision: decisionName,
            outputPortName: outputPortName,
            fieldValues: fields,
            reason: request.Reason,
            reasons: request.Reasons,
            actions: request.Actions,
            contextInputs: contextInputs,
            workflowInputs: workflowInputs);

        try
        {
            var rendered = templateRenderer.Render(template, scope, cancellationToken);
            return new HitlRenderResult(rendered, null);
        }
        catch (CodeFlow.Runtime.PromptTemplateException ex)
        {
            return new HitlRenderResult(null, $"Decision output template failed: {ex.Message}");
        }
    }

    private static IReadOnlyDictionary<string, JsonElement> DeserializeInputsJson(string? inputsJson)
    {
        if (string.IsNullOrWhiteSpace(inputsJson))
        {
            return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        }

        try
        {
            using var document = JsonDocument.Parse(inputsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            }

            var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                result[property.Name] = property.Value.Clone();
            }
            return result;
        }
        catch (JsonException)
        {
            return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        }
    }

    private static string BuildDefaultOutput(HitlDecisionRequest request)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"HITL decision: {request.OutputPortName}");

        if (!string.IsNullOrWhiteSpace(request.Reason))
        {
            builder.AppendLine(request.Reason);
        }

        if (request.Actions is { Count: > 0 })
        {
            builder.AppendLine("Actions:");
            foreach (var action in request.Actions)
            {
                builder.AppendLine($" - {action}");
            }
        }

        if (request.Reasons is { Count: > 0 })
        {
            builder.AppendLine("Reasons:");
            foreach (var reason in request.Reasons)
            {
                builder.AppendLine($" - {reason}");
            }
        }

        return builder.ToString().Trim();
    }

    private static JsonElement BuildDecisionPayload(HitlDecisionRequest request, string outputPortName)
    {
        var json = new JsonObject
        {
            ["portName"] = outputPortName
        };

        if (!string.IsNullOrWhiteSpace(request.Reason))
        {
            json["reason"] = request.Reason;
        }

        if (request.Actions is { Count: > 0 })
        {
            json["actions"] = new JsonArray(request.Actions
                .Select(value => (JsonNode?)JsonValue.Create(value))
                .ToArray());
        }

        if (request.Reasons is { Count: > 0 })
        {
            json["reasons"] = new JsonArray(request.Reasons
                .Select(value => (JsonNode?)JsonValue.Create(value))
                .ToArray());
        }

        using var document = JsonDocument.Parse(json.ToJsonString());
        return document.RootElement.Clone();
    }

    private static JsonElement? ParsePayload(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// sc-273 — bulk-resolve <see cref="TraceDecisionDto.VerdictSource"/> for the agents that
    /// produced decisions in a trace. One round-trip per call regardless of trace size.
    ///
    /// <list type="bullet">
    ///   <item><description>An agent with any host grant for <c>run_command</c> or
    ///   <c>apply_patch</c> is tagged <c>"mechanical"</c> — its decisions are gated by
    ///   deterministic command execution.</description></item>
    ///   <item><description>An agent with NO host-tool grants at all is tagged
    ///   <c>"model"</c> — its decisions came from the LLM's response to its prompt.</description></item>
    ///   <item><description>Anything else (e.g. <c>read_file</c>-only inspectors) maps to
    ///   <c>null</c> — the timeline omits the badge for those.</description></item>
    /// </list>
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, string?>> ResolveVerdictSourcesAsync(
        CodeFlowDbContext dbContext,
        IEnumerable<string> agentKeys,
        CancellationToken cancellationToken)
    {
        var distinctKeys = agentKeys
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (distinctKeys.Length == 0)
        {
            return new Dictionary<string, string?>(StringComparer.Ordinal);
        }

        var hostGrantsByAgent = await (
            from assignment in dbContext.AgentRoleAssignments.AsNoTracking()
            join grant in dbContext.AgentRoleToolGrants.AsNoTracking()
                on assignment.RoleId equals grant.RoleId
            where distinctKeys.Contains(assignment.AgentKey)
                && !assignment.Role.IsArchived
                && grant.Category == AgentRoleToolCategory.Host
            select new { assignment.AgentKey, grant.ToolIdentifier })
            .ToListAsync(cancellationToken);

        var grantsByAgent = hostGrantsByAgent
            .GroupBy(g => g.AgentKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(x => x.ToolIdentifier).ToHashSet(StringComparer.OrdinalIgnoreCase),
                StringComparer.Ordinal);

        var result = new Dictionary<string, string?>(distinctKeys.Length, StringComparer.Ordinal);
        foreach (var key in distinctKeys)
        {
            if (!grantsByAgent.TryGetValue(key, out var grants))
            {
                // No host-tool grants at all → pure LLM agent → "model".
                result[key] = "model";
                continue;
            }

            if (grants.Contains("run_command") || grants.Contains("apply_patch"))
            {
                result[key] = "mechanical";
            }
            else
            {
                // Has some host grants (e.g. read_file-only inspector) but nothing exec-class
                // → don't claim either bucket; UI omits the badge.
                result[key] = null;
            }
        }

        return result;
    }

    private static IReadOnlyDictionary<string, JsonElement> DeserializeContextInputs(string? inputsJson)
    {
        if (string.IsNullOrWhiteSpace(inputsJson))
        {
            return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        }

        using var document = JsonDocument.Parse(inputsJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        }

        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            result[property.Name] = property.Value.Clone();
        }

        return result;
    }

    // Mirrors WorkflowSagaStateMachine.ReadArtifactAsJsonAsync — kept local because the saga's
    // copy is private. Wraps non-JSON text as { "text": "…" } so InputScripts can still read it.
    private static async Task<JsonElement> ReadArtifactAsJsonAsync(
        IArtifactStore artifactStore,
        Uri inputRef,
        CancellationToken cancellationToken)
    {
        await using var stream = await artifactStore.ReadAsync(inputRef, cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: false);
        var text = await reader.ReadToEndAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(text))
        {
            return JsonDocument.Parse("{}").RootElement.Clone();
        }

        try
        {
            return JsonDocument.Parse(text).RootElement.Clone();
        }
        catch (JsonException)
        {
            var doc = new { text };
            return JsonSerializer.SerializeToElement(doc);
        }
    }

    private static IReadOnlyList<string> DeserializeLogs(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<string>();
        }

        return JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();
    }

    private static TraceSummaryDto MapSummary(WorkflowSagaStateEntity saga) => new(
        TraceId: saga.TraceId,
        WorkflowKey: saga.WorkflowKey,
        WorkflowVersion: saga.WorkflowVersion,
        CurrentState: saga.CurrentState,
        CurrentAgentKey: saga.CurrentAgentKey,
        RoundCount: saga.RoundCount,
        CreatedAtUtc: DateTime.SpecifyKind(saga.CreatedAtUtc, DateTimeKind.Utc),
        UpdatedAtUtc: DateTime.SpecifyKind(saga.UpdatedAtUtc, DateTimeKind.Utc),
        ParentTraceId: saga.ParentTraceId,
        ParentNodeId: saga.ParentNodeId,
        ParentReviewRound: saga.ParentReviewRound,
        ParentReviewMaxRounds: saga.ParentReviewMaxRounds);

    private static TraceDetailDto MapDetail(
        WorkflowSagaStateEntity saga,
        IReadOnlyList<WorkflowSagaDecisionEntity> decisions,
        IReadOnlyList<WorkflowSagaLogicEvaluationEntity> logicEvaluations,
        IReadOnlyList<HitlTaskEntity> pendingHitl,
        IReadOnlyDictionary<Guid, IReadOnlyList<string>> subflowPaths,
        IReadOnlyDictionary<string, string?> verdictSources) => new(
        TraceId: saga.TraceId,
        WorkflowKey: saga.WorkflowKey,
        WorkflowVersion: saga.WorkflowVersion,
        CurrentState: saga.CurrentState,
        CurrentAgentKey: saga.CurrentAgentKey,
        CurrentRoundId: saga.CurrentRoundId,
        RoundCount: saga.RoundCount,
        PinnedAgentVersions: saga.GetPinnedAgentVersions(),
        ContextInputs: DeserializeContextInputs(saga.InputsJson),
        Decisions: decisions
            .Select(entity => new TraceDecisionDto(
                AgentKey: entity.AgentKey,
                AgentVersion: entity.AgentVersion,
                Decision: entity.Decision,
                DecisionPayload: ParsePayload(entity.DecisionPayloadJson),
                RoundId: entity.RoundId,
                RecordedAtUtc: DateTime.SpecifyKind(entity.RecordedAtUtc, DateTimeKind.Utc),
                NodeId: entity.NodeId,
                OutputPortName: entity.OutputPortName,
                InputRef: entity.InputRef,
                OutputRef: entity.OutputRef,
                NodeEnteredAtUtc: entity.NodeEnteredAtUtc.HasValue
                    ? DateTime.SpecifyKind(entity.NodeEnteredAtUtc.Value, DateTimeKind.Utc)
                    : null,
                VerdictSource: verdictSources.GetValueOrDefault(entity.AgentKey)))
            .ToArray(),
        LogicEvaluations: logicEvaluations
            .Select(entity => new TraceLogicEvaluationDto(
                NodeId: entity.NodeId,
                OutputPortName: entity.OutputPortName,
                RoundId: entity.RoundId,
                Duration: TimeSpan.FromTicks(entity.DurationTicks),
                Logs: DeserializeLogs(entity.LogsJson),
                FailureKind: entity.FailureKind,
                FailureMessage: entity.FailureMessage,
                RecordedAtUtc: DateTime.SpecifyKind(entity.RecordedAtUtc, DateTimeKind.Utc)))
            .ToArray(),
        PendingHitl: pendingHitl
            .Select(task => MapHitl(
                task,
                originTraceId: task.TraceId,
                subflowPath: subflowPaths.TryGetValue(task.TraceId, out var path) ? path : Array.Empty<string>()))
            .ToArray(),
        CreatedAtUtc: DateTime.SpecifyKind(saga.CreatedAtUtc, DateTimeKind.Utc),
        UpdatedAtUtc: DateTime.SpecifyKind(saga.UpdatedAtUtc, DateTimeKind.Utc),
        FailureReason: saga.FailureReason);

    private static HitlTaskDto MapHitl(HitlTaskEntity task) =>
        MapHitl(task, originTraceId: null, subflowPath: null);

    private static HitlTaskDto MapHitl(
        HitlTaskEntity task,
        Guid? originTraceId,
        IReadOnlyList<string>? subflowPath) => new(
            Id: task.Id,
            TraceId: task.TraceId,
            RoundId: task.RoundId,
            AgentKey: task.AgentKey,
            AgentVersion: task.AgentVersion,
            InputRef: new Uri(task.InputRef),
            InputPreview: task.InputPreview,
            CreatedAtUtc: DateTime.SpecifyKind(task.CreatedAtUtc, DateTimeKind.Utc),
            State: task.State.ToString(),
            Decision: task.Decision,
            DecidedAtUtc: task.DecidedAtUtc is null ? null : DateTime.SpecifyKind(task.DecidedAtUtc.Value, DateTimeKind.Utc),
            DeciderId: task.DeciderId,
            OriginTraceId: originTraceId,
            SubflowPath: subflowPath);

    /// <summary>
    /// Walks up <paramref name="descendantId"/>'s <c>parent_trace_id</c> chain and returns true
    /// iff <paramref name="ancestorId"/> appears in that chain. Used by the artifact endpoint to
    /// authorize serving a descendant saga's artifact when the request is scoped to an ancestor
    /// trace (Subflow/ReviewLoop nodes surface child outputs on the parent's timeline). Bounded
    /// by <see cref="CodeFlow.Orchestration.WorkflowSagaStateMachine.MaxSubflowDepth"/> so there
    /// is no risk of unbounded recursion even if parent_trace_id columns are corrupt.
    /// </summary>
    private static async Task<bool> IsDescendantTraceAsync(
        CodeFlowDbContext dbContext,
        Guid ancestorId,
        Guid descendantId,
        CancellationToken cancellationToken)
    {
        if (ancestorId == descendantId)
        {
            return true;
        }

        var cursor = descendantId;
        for (var hops = 0; hops <= CodeFlow.Orchestration.WorkflowSagaStateMachine.MaxSubflowDepth; hops++)
        {
            var parentId = await dbContext.WorkflowSagas
                .AsNoTracking()
                .Where(s => s.TraceId == cursor)
                .Select(s => s.ParentTraceId)
                .FirstOrDefaultAsync(cancellationToken);

            if (parentId is null)
            {
                return false;
            }

            if (parentId.Value == ancestorId)
            {
                return true;
            }

            cursor = parentId.Value;
        }

        return false;
    }

    /// <summary>
    /// Breadth-first walk from the root saga down through descendants via parent_trace_id.
    /// Bounded by <see cref="CodeFlow.Orchestration.WorkflowSagaStateMachine.MaxSubflowDepth"/>
    /// so there's no risk of unbounded recursion even if the depth column is somehow corrupt.
    /// Returns the root plus every descendant saga reachable within the cap.
    /// </summary>
    private static async Task<IReadOnlyList<WorkflowSagaStateEntity>> CollectSubtreeSagasAsync(
        CodeFlowDbContext dbContext,
        WorkflowSagaStateEntity root,
        CancellationToken cancellationToken)
    {
        var all = new List<WorkflowSagaStateEntity> { root };
        var currentLevel = new List<Guid> { root.TraceId };

        for (var level = 0; level < CodeFlow.Orchestration.WorkflowSagaStateMachine.MaxSubflowDepth; level++)
        {
            if (currentLevel.Count == 0)
            {
                break;
            }

            var parents = currentLevel;
            var children = await dbContext.WorkflowSagas
                .AsNoTracking()
                .Where(s => s.ParentTraceId != null && parents.Contains(s.ParentTraceId!.Value))
                .ToListAsync(cancellationToken);

            if (children.Count == 0)
            {
                break;
            }

            all.AddRange(children);
            currentLevel = children.Select(s => s.TraceId).ToList();
        }

        return all;
    }

    /// <summary>
    /// Builds the <c>subflowPath</c> for each saga in the subtree: the ordered list of workflow
    /// keys from the immediate child of the root down to (and including) the owning saga. The
    /// root itself maps to an empty path.
    /// </summary>
    private static IReadOnlyDictionary<Guid, IReadOnlyList<string>> BuildSubflowPaths(
        Guid rootTraceId,
        IReadOnlyList<WorkflowSagaStateEntity> sagas)
    {
        var byTrace = sagas.ToDictionary(s => s.TraceId);
        var result = new Dictionary<Guid, IReadOnlyList<string>>
        {
            [rootTraceId] = Array.Empty<string>(),
        };

        foreach (var saga in sagas)
        {
            if (saga.TraceId == rootTraceId)
            {
                continue;
            }

            var path = new List<string>();
            var cursor = saga;
            var guard = CodeFlow.Orchestration.WorkflowSagaStateMachine.MaxSubflowDepth + 1;
            while (cursor.TraceId != rootTraceId && guard-- > 0)
            {
                path.Add(cursor.WorkflowKey);
                if (cursor.ParentTraceId is not Guid parentId
                    || !byTrace.TryGetValue(parentId, out var parent))
                {
                    break;
                }
                cursor = parent;
            }

            path.Reverse();
            result[saga.TraceId] = path;
        }

        return result;
    }

    private static async Task<int> DeleteTracesAsync(
        CodeFlowDbContext dbContext,
        IReadOnlyCollection<WorkflowSagaStateEntity> sagas,
        IOptions<WorkspaceOptions> workspaceOptions,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (sagas.Count == 0)
        {
            return 0;
        }

        var traceIds = sagas.Select(saga => saga.TraceId).ToArray();
        var correlationIds = sagas.Select(saga => saga.CorrelationId).ToArray();

        var hitlTasks = await dbContext.HitlTasks
            .Where(task => traceIds.Contains(task.TraceId))
            .ToListAsync(cancellationToken);
        if (hitlTasks.Count > 0)
        {
            dbContext.HitlTasks.RemoveRange(hitlTasks);
        }

        var decisions = await dbContext.WorkflowSagaDecisions
            .Where(decision => correlationIds.Contains(decision.SagaCorrelationId))
            .ToListAsync(cancellationToken);
        if (decisions.Count > 0)
        {
            dbContext.WorkflowSagaDecisions.RemoveRange(decisions);
        }

        var logicEvaluations = await dbContext.WorkflowSagaLogicEvaluations
            .Where(evaluation => correlationIds.Contains(evaluation.SagaCorrelationId))
            .ToListAsync(cancellationToken);
        if (logicEvaluations.Count > 0)
        {
            dbContext.WorkflowSagaLogicEvaluations.RemoveRange(logicEvaluations);
        }

        dbContext.WorkflowSagas.RemoveRange(sagas);
        await dbContext.SaveChangesAsync(cancellationToken);

        TryRemoveTraceWorkdirs(sagas, workspaceOptions, loggerFactory);

        return sagas.Count;
    }

    // Best-effort cleanup of per-trace working directories after the DB rows have been removed.
    // Subflow children share the parent's workdir, so only top-level sagas trigger the delete.
    // All failures (missing dir, IO/permission errors) are swallowed and logged by
    // TraceWorkdirCleanup.TryRemove — the API call must not fail because filesystem cleanup
    // couldn't complete. The periodic sweep catches anything left behind.
    private static void TryRemoveTraceWorkdirs(
        IReadOnlyCollection<WorkflowSagaStateEntity> sagas,
        IOptions<WorkspaceOptions> workspaceOptions,
        ILoggerFactory loggerFactory)
    {
        var topLevel = sagas.Where(s => s.ParentTraceId is null).ToArray();
        if (topLevel.Length == 0)
        {
            return;
        }

        var workingDirectoryRoot = workspaceOptions.Value.WorkingDirectoryRoot;
        var logger = loggerFactory.CreateLogger(typeof(TracesEndpoints));
        foreach (var saga in topLevel)
        {
            CodeFlow.Runtime.Workspace.TraceWorkdirCleanup.TryRemove(
                workingDirectoryRoot,
                saga.TraceId,
                logger);
        }
    }

    /// <summary>
    /// sc-274 phase 3 — emit a <see cref="RefusalStages.Preflight"/> refusal when the workflow
    /// launch assessor refuses the request. Workflow launches don't have a TraceId yet (we're
    /// refusing BEFORE generating one) and they're not tied to an assistant conversation, so
    /// both nullable correlation fields are null. The workflow key rides in <c>Path</c> so
    /// governance queries can slice "preflight refusals on workflow X" without parsing detail.
    /// Reuses the shared <see cref="PreflightRefusalDetail"/> shape so phase 1 + 2 + 3 all
    /// write the same DetailJson schema.
    /// </summary>
    private static async Task EmitWorkflowPreflightRefusalAsync(
        IRefusalEventSink sink,
        string workflowKey,
        IntentClarityAssessment assessment,
        CancellationToken cancellationToken)
    {
        try
        {
            await sink.RecordAsync(
                new RefusalEvent(
                    Id: Guid.NewGuid(),
                    TraceId: null,
                    AssistantConversationId: null,
                    Stage: RefusalStages.Preflight,
                    Code: "workflow-preflight-ambiguous",
                    Reason: $"Workflow launch did not meet the {assessment.Mode} clarity threshold "
                            + $"({assessment.OverallScore:0.00} < {assessment.Threshold:0.00}).",
                    Axis: PreflightRefusalDetail.LowestDimensionAxis(assessment),
                    Path: workflowKey,
                    DetailJson: PreflightRefusalDetail.Build(assessment).ToJsonString(),
                    OccurredAt: DateTimeOffset.UtcNow),
                cancellationToken);
        }
        catch
        {
            // Refusal recording is observability — never break the launch flow on sink failure.
            // The HTTP 422 still flows to the caller.
        }
    }

    private static WorkflowPreflightRefusalResponse BuildWorkflowPreflightResponse(
        string workflowKey,
        IntentClarityAssessment assessment) =>
        new(
            WorkflowKey: workflowKey,
            Code: "workflow-preflight-ambiguous",
            Mode: assessment.Mode.ToString(),
            OverallScore: assessment.OverallScore,
            Threshold: assessment.Threshold,
            Dimensions: assessment.Dimensions
                .Select(d => new PreflightDimensionDto(d.Dimension, d.Score, d.Reason))
                .ToArray(),
            MissingFields: assessment.MissingFields,
            ClarificationQuestions: assessment.ClarificationQuestions);
}

internal static class TraceEventJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new System.Text.Json.Serialization.JsonStringEnumConverter()
        }
    };
}
