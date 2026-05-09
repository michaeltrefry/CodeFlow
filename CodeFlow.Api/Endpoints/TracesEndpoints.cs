using CodeFlow.Api.Auth;
using CodeFlow.Api.Dtos;
using CodeFlow.Api.Handlers;
using CodeFlow.Api.Mapping;
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

        return Results.Ok(sagas.Select(s => s.ToSummaryDto()).ToArray());
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
            dbContext,
            decisions.Select(d => new AgentVersionKey(d.AgentKey, d.AgentVersion)),
            cancellationToken);

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
            dbContext,
            decisions.Select(d => new AgentVersionKey(d.AgentKey, d.AgentVersion)),
            cancellationToken);

        var response = descendants
            .Select(descendant => new TraceDescendantDto(
                Summary: descendant.ToSummaryDto(),
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
    private static Task<IResult> CreateTraceAsync(
        CreateTraceRequest request,
        CreateTraceHandler handler,
        CancellationToken cancellationToken)
        => handler.ExecuteAsync(request, cancellationToken);

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

        return Results.Ok(tasks.Select(t => t.ToHitlDto()).ToArray());
    }

    private static Task<IResult> SubmitHitlDecisionAsync(
        Guid id,
        HitlDecisionRequest request,
        SubmitHitlDecisionHandler handler,
        CancellationToken cancellationToken)
        => handler.ExecuteAsync(id, request, cancellationToken);

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
    private static async Task<IReadOnlyDictionary<AgentVersionKey, string?>> ResolveVerdictSourcesAsync(
        CodeFlowDbContext dbContext,
        IEnumerable<AgentVersionKey> agentKeys,
        CancellationToken cancellationToken)
    {
        var distinctPairs = agentKeys
            .Where(k => !string.IsNullOrWhiteSpace(k.AgentKey))
            .Distinct()
            .ToArray();
        if (distinctPairs.Length == 0)
        {
            return new Dictionary<AgentVersionKey, string?>();
        }

        // EF/MariaDB doesn't translate `Contains` over a tuple list; flatten to two parallel
        // arrays and filter+match in-memory after a key-only DB query. The per-saga decision
        // count is small (decisions of a single saga / subtree), so this is cheap.
        var distinctAgentKeys = distinctPairs
            .Select(p => p.AgentKey)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var allowedPairs = new HashSet<AgentVersionKey>(distinctPairs);

        var hostGrantsByAgent = await (
            from assignment in dbContext.AgentRoleAssignments.AsNoTracking()
            join grant in dbContext.AgentRoleToolGrants.AsNoTracking()
                on assignment.RoleId equals grant.RoleId
            where distinctAgentKeys.Contains(assignment.AgentKey)
                && !assignment.Role.IsArchived
                && grant.Category == AgentRoleToolCategory.Host
            select new { assignment.AgentKey, assignment.AgentVersion, grant.ToolIdentifier })
            .ToListAsync(cancellationToken);

        var grantsByAgent = hostGrantsByAgent
            .Where(g => allowedPairs.Contains(new AgentVersionKey(g.AgentKey, g.AgentVersion)))
            .GroupBy(g => new AgentVersionKey(g.AgentKey, g.AgentVersion))
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => x.ToolIdentifier).ToHashSet(StringComparer.OrdinalIgnoreCase));

        var result = new Dictionary<AgentVersionKey, string?>(distinctPairs.Length);
        foreach (var pair in distinctPairs)
        {
            if (!grantsByAgent.TryGetValue(pair, out var grants))
            {
                // No host-tool grants at all → pure LLM agent → "model".
                result[pair] = "model";
                continue;
            }

            if (grants.Contains("run_command") || grants.Contains("apply_patch"))
            {
                result[pair] = "mechanical";
            }
            else
            {
                // Has some host grants (e.g. read_file-only inspector) but nothing exec-class
                // → don't claim either bucket; UI omits the badge.
                result[pair] = null;
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

    private static IReadOnlyList<string> DeserializeLogs(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<string>();
        }

        return JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();
    }

    private static TraceDetailDto MapDetail(
        WorkflowSagaStateEntity saga,
        IReadOnlyList<WorkflowSagaDecisionEntity> decisions,
        IReadOnlyList<WorkflowSagaLogicEvaluationEntity> logicEvaluations,
        IReadOnlyList<HitlTaskEntity> pendingHitl,
        IReadOnlyDictionary<Guid, IReadOnlyList<string>> subflowPaths,
        IReadOnlyDictionary<AgentVersionKey, string?> verdictSources) => new(
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
                VerdictSource: verdictSources.GetValueOrDefault(new AgentVersionKey(entity.AgentKey, entity.AgentVersion))))
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
            .Select(task => task.ToHitlDto(
                originTraceId: task.TraceId,
                subflowPath: subflowPaths.TryGetValue(task.TraceId, out var path) ? path : Array.Empty<string>()))
            .ToArray(),
        CreatedAtUtc: DateTime.SpecifyKind(saga.CreatedAtUtc, DateTimeKind.Utc),
        UpdatedAtUtc: DateTime.SpecifyKind(saga.UpdatedAtUtc, DateTimeKind.Utc),
        FailureReason: saga.FailureReason);

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
    //
    // sc-660: per-trace git-credential file lives in a sibling root and is removed in the
    // same pass; same best-effort semantics, same per-trace sweep guarantee.
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
        var credentialRoot = workspaceOptions.Value.GitCredentialRoot;
        var logger = loggerFactory.CreateLogger(typeof(TracesEndpoints));
        foreach (var saga in topLevel)
        {
            CodeFlow.Runtime.Workspace.TraceWorkdirCleanup.TryRemove(
                workingDirectoryRoot,
                saga.TraceId,
                logger);
            CodeFlow.Runtime.Workspace.GitCredentialFile.TryRemove(
                credentialRoot,
                saga.TraceId,
                logger);
        }
    }

    private readonly record struct AgentVersionKey(string AgentKey, int AgentVersion);
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
