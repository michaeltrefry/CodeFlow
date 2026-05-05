using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeFlow.Api.Auth;
using CodeFlow.Api.Dtos;
using CodeFlow.Api.Endpoints;
using CodeFlow.Contracts;
using CodeFlow.Persistence;
using CodeFlow.Runtime;
using MassTransit;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace CodeFlow.Api.Handlers;

/// <summary>
/// Owns <c>POST /api/traces/{id}/hitl-decision</c>: locates the pending HITL task, renders the
/// per-decision output (server-side template if the agent declares one, falling back to the
/// caller-supplied <c>OutputText</c> or <c>BuildDefaultOutput</c>), persists the artifact,
/// flips the task to Decided, and publishes <see cref="AgentInvocationCompleted"/> so the saga
/// resumes.
///
/// <para>
/// Carved out of <see cref="TracesEndpoints"/> (sc-168 / F-004). Same lifecycle rules as
/// <see cref="CreateTraceHandler"/>: scoped registration, all dependencies constructor-injected.
/// </para>
/// </summary>
public sealed class SubmitHitlDecisionHandler
{
    private readonly CodeFlowDbContext dbContext;
    private readonly IArtifactStore artifactStore;
    private readonly IPublishEndpoint publishEndpoint;
    private readonly IAgentConfigRepository agentConfigRepo;
    private readonly IScribanTemplateRenderer templateRenderer;
    private readonly ICurrentUser currentUser;

    public SubmitHitlDecisionHandler(
        CodeFlowDbContext dbContext,
        IArtifactStore artifactStore,
        IPublishEndpoint publishEndpoint,
        IAgentConfigRepository agentConfigRepo,
        IScribanTemplateRenderer templateRenderer,
        ICurrentUser currentUser)
    {
        this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        this.artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
        this.publishEndpoint = publishEndpoint ?? throw new ArgumentNullException(nameof(publishEndpoint));
        this.agentConfigRepo = agentConfigRepo ?? throw new ArgumentNullException(nameof(agentConfigRepo));
        this.templateRenderer = templateRenderer ?? throw new ArgumentNullException(nameof(templateRenderer));
        this.currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
    }

    public async Task<IResult> ExecuteAsync(Guid traceId, HitlDecisionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var task = await dbContext.HitlTasks
            .FirstOrDefaultAsync(
                t => t.TraceId == traceId && t.State == HitlTaskState.Pending,
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
                TokenUsage: new CodeFlow.Contracts.TokenUsage(0, 0, 0)),
            cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.Ok(new { taskId = task.Id });
    }

    private record struct HitlRenderResult(string? Text, string? Failure);

    private async Task<HitlRenderResult> RenderHitlDecisionOutputAsync(
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
        catch (PromptTemplateException ex)
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
}
