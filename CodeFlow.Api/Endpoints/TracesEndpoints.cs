using CodeFlow.Api.Auth;
using CodeFlow.Api.Dtos;
using CodeFlow.Api.TraceEvents;
using CodeFlow.Contracts;
using CodeFlow.Persistence;
using MassTransit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeFlow.Api.Endpoints;

public static class TracesEndpoints
{
    private static readonly string[] TerminalTraceStates = ["Completed", "Failed", "Escalated"];

    public static IEndpointRouteBuilder MapTracesEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/api/traces");

        group.MapGet("/", ListTracesAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.TracesRead);

        group.MapGet("/{id:guid}", GetTraceAsync)
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

        var detail = new TraceDetailDto(
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
                    OutputRef: entity.OutputRef))
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

        return Results.Ok(detail);
    }

    private static async Task<IResult> CreateTraceAsync(
        CreateTraceRequest request,
        IWorkflowRepository workflowRepository,
        IAgentConfigRepository agentRepository,
        IArtifactStore artifactStore,
        IPublishEndpoint publishEndpoint,
        CodeFlowDbContext dbContext,
        ICurrentUser currentUser,
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

        Workflow workflow;
        try
        {
            if (request.WorkflowVersion is int version)
            {
                workflow = await workflowRepository.GetAsync(request.WorkflowKey, version, cancellationToken);
            }
            else
            {
                var latest = await workflowRepository.GetLatestAsync(request.WorkflowKey, cancellationToken);
                if (latest is null)
                {
                    return Results.NotFound(new { error = $"Workflow '{request.WorkflowKey}' not found." });
                }
                workflow = latest;
            }
        }
        catch (WorkflowNotFoundException)
        {
            return Results.NotFound(new { error = $"Workflow '{request.WorkflowKey}' version {request.WorkflowVersion} not found." });
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
            });
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

        await publishEndpoint.Publish(
            new AgentInvokeRequested(
                TraceId: traceId,
                RoundId: roundId,
                WorkflowKey: workflow.Key,
                WorkflowVersion: workflow.Version,
                NodeId: startNode.Id,
                AgentKey: startNode.AgentKey,
                AgentVersion: startAgentVersion,
                InputRef: inputRef,
                ContextInputs: resolvedInputsResult.Values,
                CorrelationHeaders: new Dictionary<string, string>
                {
                    ["x-submitted-by"] = currentUser.Id ?? "unknown"
                }),
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

        await DeleteTracesAsync(dbContext, [saga], cancellationToken);

        return Results.NoContent();
    }

    private static async Task<IResult> BulkDeleteTracesAsync(
        BulkDeleteTracesRequest request,
        CodeFlowDbContext dbContext,
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
        var deletedCount = await DeleteTracesAsync(dbContext, sagas, cancellationToken);

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
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(uri) || !Uri.TryCreate(uri, UriKind.Absolute, out var artifactUri))
        {
            return Results.BadRequest(new { error = "A valid artifact URI is required." });
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
            return Results.BadRequest(new { error = "The artifact URI is not valid for this store." });
        }

        if (metadata.TraceId != id)
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

        await WriteExistingDecisionsAsync(httpContext, dbContext, id, cancellationToken);

        await foreach (var traceEvent in broker.SubscribeAsync(id, cancellationToken))
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
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        var task = await dbContext.HitlTasks
            .FirstOrDefaultAsync(
                t => t.TraceId == id && t.State == HitlTaskState.Pending,
                cancellationToken);

        if (task is null)
        {
            return Results.NotFound(new { error = "No pending HITL task for this trace." });
        }

        var startedAt = DateTimeOffset.UtcNow;
        var outputText = request.OutputText ?? BuildDefaultOutput(request);

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

        var contractsDecision = (Contracts.AgentDecisionKind)(int)request.Decision;
        var outputPortName = string.IsNullOrWhiteSpace(request.OutputPortName)
            ? AgentDecisionPorts.ToPortName(contractsDecision)
            : request.OutputPortName.Trim();
        var decisionPayload = BuildDecisionPayload(request);

        task.State = HitlTaskState.Decided;
        task.Decision = request.Decision;
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
                Decision: contractsDecision,
                DecisionPayload: decisionPayload,
                Duration: DateTimeOffset.UtcNow - startedAt,
                TokenUsage: new Contracts.TokenUsage(0, 0, 0)),
            cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.Ok(new { taskId = task.Id });
    }

    private static string BuildDefaultOutput(HitlDecisionRequest request)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"HITL decision: {request.Decision}");

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

    private static JsonElement BuildDecisionPayload(HitlDecisionRequest request)
    {
        var json = new JsonObject
        {
            ["kind"] = request.Decision.ToString()
        };

        if (!string.IsNullOrWhiteSpace(request.OutputPortName))
        {
            json["outputPortName"] = request.OutputPortName.Trim();
        }

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

        return sagas.Count;
    }
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
