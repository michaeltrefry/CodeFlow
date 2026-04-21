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

        group.MapGet("/hitl/pending", ListPendingHitlAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.TracesRead);

        group.MapPost("/{id:guid}/hitl-decision", SubmitHitlDecisionAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.HitlWrite);

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

        var pendingHitl = await dbContext.HitlTasks
            .AsNoTracking()
            .Where(task => task.TraceId == id && task.State == HitlTaskState.Pending)
            .OrderBy(task => task.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        var decisionHistory = saga.GetDecisionHistory();

        var detail = new TraceDetailDto(
            TraceId: saga.TraceId,
            WorkflowKey: saga.WorkflowKey,
            WorkflowVersion: saga.WorkflowVersion,
            CurrentState: saga.CurrentState,
            CurrentAgentKey: saga.CurrentAgentKey,
            CurrentRoundId: saga.CurrentRoundId,
            RoundCount: saga.RoundCount,
            PinnedAgentVersions: saga.GetPinnedAgentVersions(),
            Decisions: decisionHistory
                .Select(record => new TraceDecisionDto(
                    AgentKey: record.AgentKey,
                    AgentVersion: record.AgentVersion,
                    Decision: (Runtime.AgentDecisionKind)(int)record.Decision,
                    DecisionPayload: record.DecisionPayload,
                    RoundId: record.RoundId,
                    RecordedAtUtc: DateTime.SpecifyKind(record.RecordedAtUtc, DateTimeKind.Utc)))
                .ToArray(),
            PendingHitl: pendingHitl.Select(MapHitl).ToArray(),
            CreatedAtUtc: DateTime.SpecifyKind(saga.CreatedAtUtc, DateTimeKind.Utc),
            UpdatedAtUtc: DateTime.SpecifyKind(saga.UpdatedAtUtc, DateTimeKind.Utc));

        return Results.Ok(detail);
    }

    private static async Task<IResult> CreateTraceAsync(
        CreateTraceRequest request,
        IWorkflowRepository workflowRepository,
        IAgentConfigRepository agentRepository,
        IArtifactStore artifactStore,
        IPublishEndpoint publishEndpoint,
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

        return Results.Created($"/api/traces/{traceId}", new CreateTraceResponse(traceId));
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
        var saga = await dbContext.WorkflowSagas
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.TraceId == traceId, cancellationToken);

        if (saga is null)
        {
            return;
        }

        foreach (var decision in saga.GetDecisionHistory())
        {
            var traceEvent = new TraceEvent(
                TraceId: saga.TraceId,
                RoundId: decision.RoundId,
                Kind: TraceEventKind.Completed,
                AgentKey: decision.AgentKey,
                AgentVersion: decision.AgentVersion,
                OutputRef: null,
                InputRef: null,
                Decision: decision.Decision,
                DecisionPayload: decision.DecisionPayload,
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
        var decisionPayload = BuildDecisionPayload(request);

        task.State = HitlTaskState.Decided;
        task.Decision = request.Decision;
        task.DecisionPayloadJson = decisionPayload.GetRawText();
        task.DeciderId = currentUser.Id;
        task.DecidedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        await publishEndpoint.Publish(
            new AgentInvocationCompleted(
                TraceId: task.TraceId,
                RoundId: task.RoundId,
                FromNodeId: task.NodeId,
                AgentKey: task.AgentKey,
                AgentVersion: task.AgentVersion,
                OutputPortName: AgentDecisionPorts.ToPortName(contractsDecision),
                OutputRef: outputRef,
                Decision: contractsDecision,
                DecisionPayload: decisionPayload,
                Duration: DateTimeOffset.UtcNow - startedAt,
                TokenUsage: new Contracts.TokenUsage(0, 0, 0)),
            cancellationToken);

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

    private static TraceSummaryDto MapSummary(WorkflowSagaStateEntity saga) => new(
        TraceId: saga.TraceId,
        WorkflowKey: saga.WorkflowKey,
        WorkflowVersion: saga.WorkflowVersion,
        CurrentState: saga.CurrentState,
        CurrentAgentKey: saga.CurrentAgentKey,
        RoundCount: saga.RoundCount,
        CreatedAtUtc: DateTime.SpecifyKind(saga.CreatedAtUtc, DateTimeKind.Utc),
        UpdatedAtUtc: DateTime.SpecifyKind(saga.UpdatedAtUtc, DateTimeKind.Utc));

    private static HitlTaskDto MapHitl(HitlTaskEntity task) => new(
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
        DeciderId: task.DeciderId);
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
