using CodeFlow.Contracts;
using CodeFlow.Orchestration.TokenTracking;
using CodeFlow.Persistence;
using CodeFlow.Persistence.Authority;
using CodeFlow.Runtime;
using CodeFlow.Runtime.Authority;
using CodeFlow.Runtime.Observability;
using CodeFlow.Runtime.Workspace;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ContractsToolExecutionContext = CodeFlow.Contracts.ToolExecutionContext;
using ContractsToolRepositoryContext = CodeFlow.Contracts.ToolRepositoryContext;
using ContractsToolWorkspaceContext = CodeFlow.Contracts.ToolWorkspaceContext;
using RuntimeToolExecutionContext = CodeFlow.Runtime.ToolExecutionContext;
using RuntimeToolRepositoryContext = CodeFlow.Runtime.ToolRepositoryContext;
using RuntimeToolWorkspaceContext = CodeFlow.Runtime.ToolWorkspaceContext;

namespace CodeFlow.Orchestration;

public sealed class AgentInvocationConsumer : IConsumer<AgentInvokeRequested>
{
    private const int HitlInputPreviewLength = 2048;
    private const string AgentInvocationFailedReason = "AgentInvocationFailed";

    // P2: a Scriban comment block carrying the auto-inject annotation. Comments do not render at
    // model time but stay visible in the templated source so the live prompt preview (VZ3) can
    // surface the `[auto-injected]` marker by scanning for this delimiter.
    public const string AutoInjectedLastRoundReminderTemplate =
        "{{# [auto-injected] @codeflow/last-round-reminder #}}\n{{ include \"@codeflow/last-round-reminder\" }}";

    // Matches `{{ include "@codeflow/last-round-reminder" }}` (single or double quotes, any
    // surrounding whitespace) in a system prompt or user template, so we can de-dup an explicit
    // include and skip auto-injection.
    private static readonly Regex ExplicitLastRoundReminderInclude = new(
        @"{{\s*include\s+[""']@codeflow/last-round-reminder[""']\s*}}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private readonly IAgentConfigRepository agentConfigRepository;
    private readonly IArtifactStore artifactStore;
    private readonly IAgentInvoker agentInvoker;
    private readonly IRoleResolutionService roleResolution;
    private readonly CodeFlowDbContext dbContext;
    private readonly IPromptPartialRepository promptPartialRepository;
    private readonly ITokenUsageRecordRepository? tokenUsageRecords;
    private readonly IAuthoritySnapshotRecorder? authoritySnapshotRecorder;

    public AgentInvocationConsumer(
        IAgentConfigRepository agentConfigRepository,
        IArtifactStore artifactStore,
        IAgentInvoker agentInvoker,
        IRoleResolutionService roleResolution,
        CodeFlowDbContext dbContext,
        IPromptPartialRepository promptPartialRepository,
        ITokenUsageRecordRepository? tokenUsageRecords = null,
        IAuthoritySnapshotRecorder? authoritySnapshotRecorder = null)
    {
        this.agentConfigRepository = agentConfigRepository ?? throw new ArgumentNullException(nameof(agentConfigRepository));
        this.artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
        this.agentInvoker = agentInvoker ?? throw new ArgumentNullException(nameof(agentInvoker));
        this.roleResolution = roleResolution ?? throw new ArgumentNullException(nameof(roleResolution));
        this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        this.promptPartialRepository = promptPartialRepository ?? throw new ArgumentNullException(nameof(promptPartialRepository));
        // Optional so existing test fixtures that construct the consumer directly without
        // building a full DI graph keep working. Production wires them through HostExtensions.
        this.tokenUsageRecords = tokenUsageRecords;
        this.authoritySnapshotRecorder = authoritySnapshotRecorder;
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

            var resolvedPartials = await ResolvePartialsAsync(
                agentConfig.Configuration.PartialPins,
                context.CancellationToken);

            var (effectivePromptTemplate, partialsForInvocation, lastRoundReminderInjected) =
                InjectLastRoundReminderIfApplicable(
                    agentConfig.Configuration.SystemPrompt,
                    agentConfig.Configuration.PromptTemplate,
                    resolvedPartials,
                    message.ReviewRound,
                    message.OptOutLastRoundReminder);

            var invocationConfig = agentConfig.Configuration with
            {
                PromptTemplate = effectivePromptTemplate,
                Variables = AgentPromptScopeBuilder.Merge(
                    agentConfig.Configuration.Variables,
                    AgentPromptScopeBuilder.BuildContextVariables(message.ContextInputs),
                    AgentPromptScopeBuilder.BuildWorkflowVariables(message.WorkflowContext),
                    AgentPromptScopeBuilder.BuildReviewLoopVariables(
                        message.ReviewRound,
                        message.ReviewMaxRounds,
                        message.WorkflowContext),
                    AgentPromptScopeBuilder.BuildSwarmVariables(message.SwarmContext),
                    AgentPromptScopeBuilder.BuildInputVariables(input)),
                DeclaredOutputs = agentConfig.DeclaredOutputs.Count > 0
                    ? agentConfig.DeclaredOutputs
                    : null,
                ResolvedPartials = partialsForInvocation,
            };

            if (lastRoundReminderInjected)
            {
                activity?.SetTag(CodeFlowActivity.TagNames.LastRoundReminderAutoInjected, true);
            }
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

            // sc-269 PR3: resolve and persist the per-invocation authority envelope, then thread
            // the resolved envelope into the tool layer so workspace command/repo/network checks
            // and the ToolAccessPolicy can self-enforce against it. Failure to record is still
            // swallowed (snapshot persistence must never break the primary invocation), but a
            // non-null envelope is passed downstream when resolution succeeds.
            var envelopeResolution = await TryRecordAuthoritySnapshotAsync(message, context.CancellationToken);

            // Pre-resolve scope chain ONCE for this consumer call so the per-round capture
            // observer doesn't re-query the saga table on every LLM round-trip. Subflow depth is
            // capped at 3 → at most 3 lookups for nested traces, one for top-level.
            var captureObserver = await BuildTokenUsageCaptureObserverAsync(context, message, context.CancellationToken);

            var invocationResult = await agentInvoker.InvokeAsync(
                invocationConfig,
                input,
                resolvedTools,
                captureObserver,
                context.CancellationToken,
                BuildToolExecutionContext(message, envelopeResolution?.Envelope));

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

    /// <summary>
    /// sc-269 PR3 — resolve the per-invocation authority envelope, persist a snapshot row, emit
    /// a <see cref="RefusalStages.Admission"/> refusal per blocked axis, and return the
    /// resolution result so callers can thread the envelope into the tool layer for enforcement.
    /// Returns <c>null</c> when no recorder is wired (legacy test fixtures) or when resolution
    /// fails — both cases preserve the pre-envelope behaviour because tools fall back to their
    /// static configuration when the envelope is null.
    /// </summary>
    private async Task<EnvelopeResolutionResult?> TryRecordAuthoritySnapshotAsync(
        AgentInvokeRequested message,
        CancellationToken cancellationToken)
    {
        if (authoritySnapshotRecorder is null)
        {
            return null;
        }

        try
        {
            return await authoritySnapshotRecorder.ResolveAndRecordAsync(
                new AuthoritySnapshotInput(
                    AgentKey: message.AgentKey,
                    TraceId: message.TraceId,
                    RoundId: message.RoundId,
                    AgentVersion: message.AgentVersion,
                    WorkflowKey: message.WorkflowKey,
                    WorkflowVersion: message.WorkflowVersion),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Snapshot recording must never break the calling invocation. Capture activity tag
            // so the failure is still observable without blocking the agent run. Tool-layer
            // enforcement degrades gracefully — a null envelope means "no opinion expressed",
            // which preserves pre-PR3 behaviour.
            Activity.Current?.AddEvent(new ActivityEvent(
                "authority-snapshot-failed",
                tags: new ActivityTagsCollection
                {
                    { "exception.type", ex.GetType().FullName },
                    { "exception.message", ex.Message }
                }));
            return null;
        }
    }

    private async Task<IInvocationObserver?> BuildTokenUsageCaptureObserverAsync(
        ConsumeContext<AgentInvokeRequested> context,
        AgentInvokeRequested message,
        CancellationToken cancellationToken)
    {
        if (tokenUsageRecords is null)
        {
            return null;
        }

        var scope = await SagaScopeChainResolver.ResolveAsync(dbContext, message.TraceId, cancellationToken);
        return new TokenUsageCaptureObserver(
            tokenUsageRecords,
            rootTraceId: scope.RootTraceId,
            nodeId: message.NodeId,
            scopeChain: scope.ScopeChain,
            // ConsumeContext implements IPublishEndpoint, so handing it through gives us
            // correlated TokenUsageRecorded publishes onto the existing fabric — no separate
            // IBus / IPublishEndpoint needs to be injected.
            publishEndpoint: context);
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

    /// <summary>
    /// Resolve the agent's partial pins to concrete bodies via the persistence layer. Returns
    /// null when the agent declares no pins so the renderer keeps its no-loader fast path. A pin
    /// pointing at a missing (key, version) silently falls through here — the renderer will surface
    /// the missing include with the offending name when it reaches the <c>{{ include }}</c> call.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>?> ResolvePartialsAsync(
        IReadOnlyList<PromptPartialPin>? pins,
        CancellationToken cancellationToken)
    {
        if (pins is not { Count: > 0 })
        {
            return null;
        }

        var pinTuples = pins.Select(p => (p.Key, p.Version)).ToArray();
        var bodies = await promptPartialRepository.ResolveBodiesAsync(pinTuples, cancellationToken);
        return bodies.Count == 0 ? null : bodies;
    }

    /// <summary>
    /// P2: when an agent runs inside a ReviewLoop child saga (<paramref name="reviewRound"/>
    /// is non-null), append <c>@codeflow/last-round-reminder</c> to the prompt template and seed
    /// its body into the partials map so the include resolves at render time.
    ///
    /// Skip cases:
    /// - The agent is not in a ReviewLoop (no round binding to anchor the reminder).
    /// - The author opted out at the workflow node (<paramref name="optOut"/>).
    /// - The agent already includes the partial explicitly in its system prompt or prompt template
    ///   (de-dup so authors who opted in by hand pre-P2 don't get the reminder twice).
    ///
    /// When injection happens and the agent didn't pin the partial, fall back to the bundled
    /// <see cref="SystemPromptPartials.LastRoundReminderKey"/> body — the seeded version 1 is the
    /// runtime contract for the auto-injected case.
    /// </summary>
    public static (string? PromptTemplate,
        IReadOnlyDictionary<string, string>? Partials,
        bool Injected) InjectLastRoundReminderIfApplicable(
        string? systemPrompt,
        string? promptTemplate,
        IReadOnlyDictionary<string, string>? resolvedPartials,
        int? reviewRound,
        bool optOut)
    {
        if (reviewRound is null || optOut)
        {
            return (promptTemplate, resolvedPartials, false);
        }

        if (HasExplicitLastRoundReminder(systemPrompt) || HasExplicitLastRoundReminder(promptTemplate))
        {
            return (promptTemplate, resolvedPartials, false);
        }

        var augmentedTemplate = string.IsNullOrEmpty(promptTemplate)
            ? AutoInjectedLastRoundReminderTemplate
            : promptTemplate + "\n\n" + AutoInjectedLastRoundReminderTemplate;

        var augmentedPartials = EnsureLastRoundReminderBody(resolvedPartials);

        return (augmentedTemplate, augmentedPartials, true);
    }

    private static bool HasExplicitLastRoundReminder(string? template)
    {
        return !string.IsNullOrEmpty(template)
            && ExplicitLastRoundReminderInclude.IsMatch(template);
    }

    private static IReadOnlyDictionary<string, string> EnsureLastRoundReminderBody(
        IReadOnlyDictionary<string, string>? resolvedPartials)
    {
        if (resolvedPartials is not null
            && resolvedPartials.ContainsKey(SystemPromptPartials.LastRoundReminderKey))
        {
            return resolvedPartials;
        }

        var seed = SystemPromptPartials.All
            .First(p => p.Key == SystemPromptPartials.LastRoundReminderKey)
            .Body;

        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        if (resolvedPartials is not null)
        {
            foreach (var entry in resolvedPartials)
            {
                merged[entry.Key] = entry.Value;
            }
        }

        merged[SystemPromptPartials.LastRoundReminderKey] = seed;
        return merged;
    }

    // Tool plumbing for an agent invocation. Code-aware workflows expose a per-trace working
    // directory through `workflow.workDir` (seeded by `TracesEndpoints.CreateTraceAsync`); when
    // present, that path-jails every host tool to the trace workdir and supersedes the legacy
    // per-repo `ToolExecutionContext` carried on the message. Non-code workflows fall through
    // to the legacy plumbing unchanged. The resolved authority envelope (sc-269 PR3) rides
    // alongside both shapes so the tool layer can self-enforce its axes.
    private static RuntimeToolExecutionContext? BuildToolExecutionContext(
        AgentInvokeRequested message,
        WorkflowExecutionEnvelope? envelope)
    {
        if (TryGetWorkflowWorkDir(message.WorkflowContext, out var workDir))
        {
            return new RuntimeToolExecutionContext(
                new RuntimeToolWorkspaceContext(message.TraceId, workDir),
                ExtractRepositoryContexts(message.ContextInputs),
                envelope);
        }

        return MapToolExecutionContext(message.ToolExecutionContext, envelope);
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

    private static RuntimeToolExecutionContext? MapToolExecutionContext(
        ContractsToolExecutionContext? context,
        WorkflowExecutionEnvelope? envelope)
    {
        // When neither the legacy contract context nor an envelope has anything to say, return
        // null so the tool layer keeps its no-context fast path. Otherwise build a shell that
        // carries whichever pieces are present (workspace, repos, envelope).
        if (context is null && envelope is null)
        {
            return null;
        }

        return new RuntimeToolExecutionContext(
            MapWorkspaceContext(context?.Workspace),
            MapRepositoryContexts(context?.Repositories),
            envelope);
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

    private static IReadOnlyList<RuntimeToolRepositoryContext>? MapRepositoryContexts(
        IReadOnlyList<ContractsToolRepositoryContext>? repositories)
    {
        if (repositories is null || repositories.Count == 0)
        {
            return null;
        }

        return repositories
            .Select(repo => new RuntimeToolRepositoryContext(
                repo.Owner,
                repo.Name,
                repo.Url,
                repo.RepoIdentityKey,
                repo.RepoSlug))
            .ToArray();
    }

    private static IReadOnlyList<RuntimeToolRepositoryContext>? ExtractRepositoryContexts(
        IReadOnlyDictionary<string, JsonElement>? contextInputs)
    {
        if (contextInputs is null
            || !contextInputs.TryGetValue("repositories", out var repositories)
            || repositories.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var result = new List<RuntimeToolRepositoryContext>();
        foreach (var entry in repositories.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("url", out var urlElement)
                || urlElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var url = urlElement.GetString();
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            try
            {
                var repo = RepoReference.Parse(url);
                result.Add(new RuntimeToolRepositoryContext(
                    repo.Owner,
                    repo.Name,
                    url,
                    repo.IdentityKey,
                    repo.Slug));
            }
            catch (ArgumentException)
            {
            }
        }

        return result.Count == 0 ? null : result;
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
