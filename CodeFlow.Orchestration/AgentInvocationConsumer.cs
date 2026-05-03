using CodeFlow.Contracts;
using CodeFlow.Contracts.Notifications;
using CodeFlow.Orchestration.Notifications;
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
    private readonly IHitlNotificationActionUrlBuilder? hitlActionUrlBuilder;

    public AgentInvocationConsumer(
        IAgentConfigRepository agentConfigRepository,
        IArtifactStore artifactStore,
        IAgentInvoker agentInvoker,
        IRoleResolutionService roleResolution,
        CodeFlowDbContext dbContext,
        IPromptPartialRepository promptPartialRepository,
        ITokenUsageRecordRepository? tokenUsageRecords = null,
        IAuthoritySnapshotRecorder? authoritySnapshotRecorder = null,
        IHitlNotificationActionUrlBuilder? hitlActionUrlBuilder = null)
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
        // Null when notifications are unconfigured (e.g. test fixtures without a public base
        // URL); CreateHitlTaskAsync silently skips the publish in that case so legacy tests
        // that construct the consumer directly continue to pass.
        this.hitlActionUrlBuilder = hitlActionUrlBuilder;
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
                var (created, preview) = await CreateHitlTaskAsync(message, input, context.CancellationToken);
                if (created is not null)
                {
                    await PublishHitlTaskPendingAsync(context, message, created, preview, context.CancellationToken);
                }
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
    // directory through the typed saga field <see cref="AgentInvokeRequested.TraceWorkDir"/>
    // (sc-593); when set, every host tool is path-jailed to it and the legacy per-repo
    // <c>ToolExecutionContext</c> carried on the message is superseded. Non-code workflows fall
    // through to the legacy plumbing unchanged. The resolved authority envelope (sc-269 PR3)
    // rides alongside both shapes so the tool layer can self-enforce its axes.
    private static RuntimeToolExecutionContext? BuildToolExecutionContext(
        AgentInvokeRequested message,
        WorkflowExecutionEnvelope? envelope)
    {
        if (!string.IsNullOrWhiteSpace(message.TraceWorkDir))
        {
            return new RuntimeToolExecutionContext(
                new RuntimeToolWorkspaceContext(message.TraceId, message.TraceWorkDir),
                ResolveRepositoryContexts(message),
                envelope);
        }

        return MapToolExecutionContext(message.ToolExecutionContext, envelope);
    }

    /// <summary>
    /// Saga-field lookup for the per-trace repository allowlist.
    /// <see cref="AgentInvokeRequested.Repositories"/> is populated by the saga from
    /// <c>saga.RepositoriesJson</c> on every dispatch and inherited across subflow boundaries,
    /// so it's the source of truth. sc-607 dropped the legacy <c>context.repositories</c>
    /// fallback alongside the bag-scope migration.
    /// </summary>
    private static IReadOnlyList<RuntimeToolRepositoryContext>? ResolveRepositoryContexts(AgentInvokeRequested message)
    {
        if (message.Repositories is not { Count: > 0 } sagaRepos)
        {
            return null;
        }

        var result = new List<RuntimeToolRepositoryContext>(sagaRepos.Count);
        foreach (var entry in sagaRepos)
        {
            if (string.IsNullOrWhiteSpace(entry.Url))
            {
                continue;
            }

            try
            {
                var repo = RepoReference.Parse(entry.Url);
                result.Add(new RuntimeToolRepositoryContext(
                    repo.Owner,
                    repo.Name,
                    entry.Url,
                    repo.IdentityKey,
                    repo.Slug));
            }
            catch (ArgumentException)
            {
            }
        }

        return result.Count > 0 ? result : null;
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

    private async Task<(HitlTaskEntity? Created, string? Preview)> CreateHitlTaskAsync(
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
            // Re-delivery of the same AgentInvokeRequested — the HITL row already exists, so
            // the original publish (or a prior redelivery's publish) already fired. Returning
            // null here keeps notifications idempotent at the saga level; the dispatcher's
            // unique index in sc-51 is the second line of defence.
            return (null, preview);
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

        return (entity, preview);
    }

    private async Task PublishHitlTaskPendingAsync(
        ConsumeContext<AgentInvokeRequested> context,
        AgentInvokeRequested message,
        HitlTaskEntity entity,
        string? preview,
        CancellationToken cancellationToken)
    {
        if (hitlActionUrlBuilder is null)
        {
            // Notification subsystem not wired (e.g. legacy test harness without a public base
            // URL configured) — skip the publish silently so the saga's HITL behaviour matches
            // pre-sc-53 expectations.
            return;
        }

        Uri actionUrl;
        try
        {
            actionUrl = hitlActionUrlBuilder.BuildForPendingTask(entity.Id, entity.TraceId);
        }
        catch (Exception ex)
        {
            // The URL builder throws when PublicBaseUrl is not configured. Failing the consumer
            // would just trigger a retry over a row that already exists, so swallow the
            // exception, attach a span event for visibility, and skip the publish. Operators
            // see the misconfiguration via the absence of notifications + the span event.
            Activity.Current?.AddEvent(new ActivityEvent(
                "hitl.notification.publish_skipped",
                tags: new ActivityTagsCollection
                {
                    { "exception.message", ex.Message },
                    { "hitl.task_id", entity.Id }
                }));
            return;
        }

        var evt = new HitlTaskPendingEvent(
            EventId: Guid.NewGuid(),
            OccurredAtUtc: DateTimeOffset.UtcNow,
            ActionUrl: actionUrl,
            Severity: NotificationSeverity.Normal,
            HitlTaskId: entity.Id,
            TraceId: message.TraceId,
            RoundId: message.RoundId,
            NodeId: message.NodeId,
            WorkflowKey: message.WorkflowKey,
            WorkflowVersion: message.WorkflowVersion,
            AgentKey: message.AgentKey,
            AgentVersion: message.AgentVersion,
            HitlTaskCreatedAtUtc: new DateTimeOffset(DateTime.SpecifyKind(entity.CreatedAtUtc, DateTimeKind.Utc)),
            InputPreview: preview,
            InputRef: message.InputRef);

        await context.Publish(evt, cancellationToken);
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
