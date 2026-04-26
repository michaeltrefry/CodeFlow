using CodeFlow.Contracts;
using CodeFlow.Persistence;
using CodeFlow.Runtime;
using CodeFlow.Runtime.Observability;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContractsToolExecutionContext = CodeFlow.Contracts.ToolExecutionContext;
using ContractsToolWorkspaceContext = CodeFlow.Contracts.ToolWorkspaceContext;
using RuntimeToolExecutionContext = CodeFlow.Runtime.ToolExecutionContext;
using RuntimeToolWorkspaceContext = CodeFlow.Runtime.ToolWorkspaceContext;

namespace CodeFlow.Orchestration;

public sealed class AgentInvocationConsumer : IConsumer<AgentInvokeRequested>
{
    private const int HitlInputPreviewLength = 2048;
    private const string AgentInvocationFailedReason = "AgentInvocationFailed";

    private readonly IAgentConfigRepository agentConfigRepository;
    private readonly IArtifactStore artifactStore;
    private readonly IAgentInvoker agentInvoker;
    private readonly IRoleResolutionService roleResolution;
    private readonly CodeFlowDbContext dbContext;

    public AgentInvocationConsumer(
        IAgentConfigRepository agentConfigRepository,
        IArtifactStore artifactStore,
        IAgentInvoker agentInvoker,
        IRoleResolutionService roleResolution,
        CodeFlowDbContext dbContext)
    {
        this.agentConfigRepository = agentConfigRepository ?? throw new ArgumentNullException(nameof(agentConfigRepository));
        this.artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
        this.agentInvoker = agentInvoker ?? throw new ArgumentNullException(nameof(agentInvoker));
        this.roleResolution = roleResolution ?? throw new ArgumentNullException(nameof(roleResolution));
        this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task Consume(ConsumeContext<AgentInvokeRequested> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var message = context.Message;
        using var activity = CodeFlowActivity.StartWorkflowRoot(
            "agent.invocation.consume",
            message.TraceId,
            ActivityKind.Consumer);
        activity?.SetTag(CodeFlowActivity.TagNames.RoundId, message.RoundId);
        activity?.SetTag(CodeFlowActivity.TagNames.WorkflowKey, message.WorkflowKey);
        activity?.SetTag(CodeFlowActivity.TagNames.WorkflowVersion, message.WorkflowVersion);
        activity?.SetTag(CodeFlowActivity.TagNames.AgentKey, message.AgentKey);
        activity?.SetTag(CodeFlowActivity.TagNames.AgentVersion, message.AgentVersion);

        var startedAt = DateTimeOffset.UtcNow;
        string? input = null;

        try
        {
            var agentConfig = await agentConfigRepository.GetAsync(
                message.AgentKey,
                message.AgentVersion,
                context.CancellationToken);

            await using var inputStream = await artifactStore.ReadAsync(
                message.InputRef,
                context.CancellationToken);
            input = await ReadInputAsync(inputStream, context.CancellationToken);

            if (agentConfig.Kind == AgentKind.Hitl)
            {
                await CreateHitlTaskAsync(message, input, context.CancellationToken);
                return;
            }

            var invocationConfig = agentConfig.Configuration with
            {
                Variables = MergeVariables(
                    agentConfig.Configuration.Variables,
                    BuildContextTemplateVariables(message.ContextInputs),
                    BuildWorkflowTemplateVariables(message.WorkflowContext),
                    BuildReviewLoopTemplateVariables(message.ReviewRound, message.ReviewMaxRounds),
                    BuildInputTemplateVariables(input)),
                DeclaredOutputs = agentConfig.DeclaredOutputs.Count > 0
                    ? agentConfig.DeclaredOutputs
                    : null
            };
            if (message.RetryContext is { } retryContext)
            {
                invocationConfig = invocationConfig with
                {
                    RetryContext = new Runtime.RetryContext(
                        retryContext.AttemptNumber,
                        retryContext.PriorFailureReason,
                        retryContext.PriorAttemptSummary)
                };
                activity?.SetTag(CodeFlowActivity.TagNames.RetryAttempt, retryContext.AttemptNumber);
            }

            var resolvedTools = await roleResolution.ResolveAsync(message.AgentKey, context.CancellationToken);

            var invocationResult = await agentInvoker.InvokeAsync(
                invocationConfig,
                input,
                resolvedTools,
                context.CancellationToken,
                BuildToolExecutionContext(message));

            await PublishCompletionAsync(
                context,
                message,
                invocationResult,
                $"{message.AgentKey}-output.txt",
                DateTimeOffset.UtcNow - startedAt);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !context.CancellationToken.IsCancellationRequested)
        {
            activity?.SetTag(CodeFlowActivity.TagNames.FailureReason, ex.Message);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);

            var httpDiagnosticsRef = await TryWriteHttpDiagnosticsArtifactAsync(
                message,
                ex,
                context.CancellationToken);

            var failureResult = new AgentInvocationResult(
                Output: BuildInvocationFailureOutput(ex, httpDiagnosticsRef),
                Decision: new AgentDecision(
                    "Failed",
                    BuildFailureDecisionPayload(ex, httpDiagnosticsRef, BuildFailureReason(ex))),
                Transcript: [],
                TokenUsage: null,
                ToolCallsExecuted: 0);

            await PublishCompletionAsync(
                context,
                message,
                failureResult,
                $"{message.AgentKey}-error.txt",
                DateTimeOffset.UtcNow - startedAt);
        }
    }

    private async Task<Uri?> TryWriteHttpDiagnosticsArtifactAsync(
        AgentInvokeRequested message,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not ModelClientHttpException httpException)
        {
            return null;
        }

        try
        {
            await using var diagnosticsStream = new MemoryStream(Encoding.UTF8.GetBytes(httpException.BuildDiagnosticsText()));
            return await artifactStore.WriteAsync(
                diagnosticsStream,
                new ArtifactMetadata(
                    message.TraceId,
                    message.RoundId,
                    Guid.NewGuid(),
                    ContentType: "text/plain",
                    FileName: $"{message.AgentKey}-http-diagnostics.txt"),
                cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    private async Task PublishCompletionAsync(
        ConsumeContext<AgentInvokeRequested> context,
        AgentInvokeRequested message,
        AgentInvocationResult invocationResult,
        string outputFileName,
        TimeSpan duration)
    {
        await using var outputStream = new MemoryStream(Encoding.UTF8.GetBytes(invocationResult.Output));
        var outputRef = await artifactStore.WriteAsync(
            outputStream,
            new ArtifactMetadata(
                message.TraceId,
                message.RoundId,
                Guid.NewGuid(),
                ContentType: "text/plain",
                FileName: outputFileName),
            context.CancellationToken);

        await context.Publish(
            new AgentInvocationCompleted(
                TraceId: message.TraceId,
                RoundId: message.RoundId,
                FromNodeId: message.NodeId,
                AgentKey: message.AgentKey,
                AgentVersion: message.AgentVersion,
                OutputPortName: invocationResult.Decision.PortName,
                OutputRef: outputRef,
                DecisionPayload: BuildDecisionPayload(invocationResult.Decision, invocationResult),
                Duration: duration,
                TokenUsage: MapTokenUsage(invocationResult.TokenUsage),
                ContextUpdates: invocationResult.ContextUpdates,
                WorkflowUpdates: invocationResult.WorkflowUpdates),
            context.CancellationToken);
    }

    private static string BuildInvocationFailureOutput(Exception ex, Uri? httpDiagnosticsRef)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Agent invocation failed.");
        builder.Append("Exception: ").AppendLine(ex.GetType().FullName ?? ex.GetType().Name);
        builder.Append("Message: ").AppendLine(BuildFailureSummary(ex));
        if (httpDiagnosticsRef is not null)
        {
            builder.AppendLine("HTTP diagnostics: available from the trace UI download link.");
        }
        return builder.ToString();
    }

    private static string BuildFailureSummary(Exception ex)
    {
        if (ex is not ModelClientHttpException httpException)
        {
            return ex.Message;
        }

        if (!string.IsNullOrWhiteSpace(httpException.ProviderErrorMessage))
        {
            return httpException.ProviderErrorMessage!;
        }

        return httpException.StatusCode is { } statusCode
            ? $"Response status code does not indicate success: {(int)statusCode} ({httpException.ResponseReasonPhrase ?? statusCode.ToString()})."
            : ex.Message;
    }

    private static string BuildFailureReason(Exception ex)
    {
        return BuildFailureSummary(ex);
    }

    private static JsonObject BuildFailureDecisionPayload(Exception ex, Uri? httpDiagnosticsRef, string reason)
    {
        var payload = new JsonObject
        {
            ["reason"] = reason,
            ["failure_code"] = AgentInvocationFailedReason,
            ["exception_type"] = ex.GetType().FullName,
            ["message"] = BuildFailureSummary(ex)
        };

        if (httpDiagnosticsRef is not null)
        {
            payload["http_diagnostics_ref"] = httpDiagnosticsRef.ToString();
        }

        if (ex is ModelClientHttpException httpException && !string.IsNullOrWhiteSpace(httpException.ProviderErrorMessage))
        {
            payload["provider_error_message"] = httpException.ProviderErrorMessage;
        }

        return payload;
    }

    private static IReadOnlyDictionary<string, string?>? MergeVariables(
        IReadOnlyDictionary<string, string?>? configured,
        IReadOnlyDictionary<string, string?> contextVariables,
        IReadOnlyDictionary<string, string?> workflowVariables,
        IReadOnlyDictionary<string, string?> reviewLoopVariables,
        IReadOnlyDictionary<string, string?> inputVariables)
    {
        if ((configured is null || configured.Count == 0)
            && contextVariables.Count == 0
            && workflowVariables.Count == 0
            && reviewLoopVariables.Count == 0
            && inputVariables.Count == 0)
        {
            return configured;
        }

        var merged = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (configured is not null)
        {
            foreach (var entry in configured)
            {
                merged[entry.Key] = entry.Value;
            }
        }

        foreach (var entry in contextVariables)
        {
            merged[entry.Key] = entry.Value;
        }

        foreach (var entry in workflowVariables)
        {
            merged[entry.Key] = entry.Value;
        }

        foreach (var entry in reviewLoopVariables)
        {
            merged[entry.Key] = entry.Value;
        }

        foreach (var entry in inputVariables)
        {
            merged[entry.Key] = entry.Value;
        }

        return merged;
    }

    private static IReadOnlyDictionary<string, string?> BuildContextTemplateVariables(
        IReadOnlyDictionary<string, JsonElement> contextInputs)
    {
        var variables = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in contextInputs)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            AddContextTemplateVariables(variables, $"context.{key}", value);
        }

        return variables;
    }

    private static IReadOnlyDictionary<string, string?> BuildWorkflowTemplateVariables(
        IReadOnlyDictionary<string, JsonElement>? workflowContext)
    {
        var variables = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (workflowContext is null)
        {
            return variables;
        }

        foreach (var (key, value) in workflowContext)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            AddContextTemplateVariables(variables, $"workflow.{key}", value);
        }

        return variables;
    }

    private static IReadOnlyDictionary<string, string?> BuildReviewLoopTemplateVariables(
        int? reviewRound,
        int? reviewMaxRounds)
    {
        // Outside a ReviewLoop, emit no template variables — an unused {{round}} placeholder in
        // a prompt will render as the literal unresolved token, matching the documented "child
        // saga not spawned by a ReviewLoop does not see these bindings" rule.
        // (Jint bindings still default to round=0/maxRounds=0/isLastRound=false so shared scripts
        // can reference them without a ReferenceError — that lives in LogicNodeScriptHost.)
        if (reviewRound is not int round || reviewMaxRounds is not int maxRounds)
        {
            return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        }

        var variables = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["round"] = round.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["maxRounds"] = maxRounds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["isLastRound"] = maxRounds > 0 && round >= maxRounds ? "true" : "false"
        };
        return variables;
    }

    private static IReadOnlyDictionary<string, string?> BuildInputTemplateVariables(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            using var document = JsonDocument.Parse(input);
            var variables = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            switch (document.RootElement.ValueKind)
            {
                case JsonValueKind.Object:
                case JsonValueKind.Array:
                    AddContextTemplateVariables(variables, "input", document.RootElement);
                    break;
            }

            return variables;
        }
        catch (JsonException)
        {
            return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void AddContextTemplateVariables(
        IDictionary<string, string?> variables,
        string key,
        JsonElement value)
    {
        variables[key] = ToTemplateValue(value);

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    AddContextTemplateVariables(variables, $"{key}.{property.Name}", property.Value);
                }
                break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in value.EnumerateArray())
                {
                    AddContextTemplateVariables(variables, $"{key}.{index}", item);
                    index += 1;
                }
                break;
        }
    }

    private static string? ToTemplateValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null => null,
            JsonValueKind.Undefined => null,
            _ => value.GetRawText()
        };
    }

    // Tool plumbing for an agent invocation. Code-aware workflows expose a per-trace working
    // directory through `workflow.workDir` (seeded by `TracesEndpoints.CreateTraceAsync`); when
    // present, that path-jails every host tool to the trace workdir and supersedes the legacy
    // per-repo `ToolExecutionContext` carried on the message. Non-code workflows fall through
    // to the legacy plumbing unchanged.
    private static RuntimeToolExecutionContext? BuildToolExecutionContext(AgentInvokeRequested message)
    {
        if (TryGetWorkflowWorkDir(message.WorkflowContext, out var workDir))
        {
            return new RuntimeToolExecutionContext(
                new RuntimeToolWorkspaceContext(message.TraceId, workDir));
        }

        return MapToolExecutionContext(message.ToolExecutionContext);
    }

    private static bool TryGetWorkflowWorkDir(
        IReadOnlyDictionary<string, JsonElement>? workflowContext,
        out string workDir)
    {
        workDir = string.Empty;
        if (workflowContext is null
            || !workflowContext.TryGetValue("workDir", out var element)
            || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var value = element.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        workDir = value;
        return true;
    }

    private static RuntimeToolExecutionContext? MapToolExecutionContext(ContractsToolExecutionContext? context)
    {
        if (context is null)
        {
            return null;
        }

        return new RuntimeToolExecutionContext(MapWorkspaceContext(context.Workspace));
    }

    private static RuntimeToolWorkspaceContext? MapWorkspaceContext(ContractsToolWorkspaceContext? workspace)
    {
        if (workspace is null)
        {
            return null;
        }

        return new RuntimeToolWorkspaceContext(
            workspace.CorrelationId,
            workspace.RootPath,
            workspace.RepoUrl,
            workspace.RepoIdentityKey,
            workspace.RepoSlug);
    }

    private static JsonObject? BuildFailureContext(
        AgentDecision failed,
        AgentInvocationResult result)
    {
        var snippet = result.Output;
        if (!string.IsNullOrWhiteSpace(snippet) && snippet.Length > 1024)
        {
            snippet = snippet[..1024];
        }

        var reason = (failed.Payload as JsonObject)?["reason"] is JsonValue value
            && value.TryGetValue<string>(out var reasonStr)
                ? reasonStr
                : null;

        return new JsonObject
        {
            ["reason"] = reason,
            ["last_output"] = snippet,
            ["tool_calls_executed"] = result.ToolCallsExecuted
        };
    }

    private async Task CreateHitlTaskAsync(
        AgentInvokeRequested message,
        string? input,
        CancellationToken cancellationToken)
    {
        var preview = input is null
            ? null
            : input.Length > HitlInputPreviewLength
                ? input[..HitlInputPreviewLength]
                : input;

        var existing = await dbContext.HitlTasks
            .FirstOrDefaultAsync(
                task => task.TraceId == message.TraceId
                    && task.RoundId == message.RoundId
                    && task.NodeId == message.NodeId
                    && task.InputRef == message.InputRef.ToString(),
                cancellationToken);

        if (existing is not null)
        {
            return;
        }

        var entity = new HitlTaskEntity
        {
            TraceId = message.TraceId,
            RoundId = message.RoundId,
            NodeId = message.NodeId,
            AgentKey = message.AgentKey,
            AgentVersion = message.AgentVersion,
            WorkflowKey = message.WorkflowKey,
            WorkflowVersion = message.WorkflowVersion,
            InputRef = message.InputRef.ToString(),
            InputPreview = preview,
            State = HitlTaskState.Pending,
            CreatedAtUtc = DateTime.UtcNow
        };

        dbContext.HitlTasks.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static async Task<string?> ReadInputAsync(
        Stream inputStream,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(inputStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var content = await reader.ReadToEndAsync(cancellationToken);

        return string.IsNullOrEmpty(content) ? null : content;
    }

    private static CodeFlow.Contracts.TokenUsage MapTokenUsage(Runtime.TokenUsage? tokenUsage)
    {
        return tokenUsage is null
            ? new CodeFlow.Contracts.TokenUsage(0, 0, 0)
            : new CodeFlow.Contracts.TokenUsage(tokenUsage.InputTokens, tokenUsage.OutputTokens, tokenUsage.TotalTokens);
    }

    private static JsonElement BuildDecisionPayload(AgentDecision decision, AgentInvocationResult result)
    {
        var json = new JsonObject
        {
            ["portName"] = decision.PortName
        };

        if (decision.Payload is not null)
        {
            json["payload"] = decision.Payload.DeepClone();
        }

        if (string.Equals(decision.PortName, "Failed", StringComparison.Ordinal))
        {
            var failureContext = BuildFailureContext(decision, result);
            if (failureContext is not null)
            {
                json["failure_context"] = failureContext;
            }
        }

        using var document = JsonDocument.Parse(json.ToJsonString());
        return document.RootElement.Clone();
    }
}
